# Cómo Exatouch (XT) inserta un pedido nuevo y lo actualiza: pagar, completar, anular, reembolsar, propinas, dividir, Pay In/Out

Referencia para ingenieros de QA que manejan la interfaz WinForms y hacen aserciones sobre filas de XTTDB. **Instantánea de origen:** el árbol `C:\Dev\Exatouch` con fecha del 7 de septiembre de 2026 (las carpetas de la aplicación no están gestionadas con git; el binario compilado `2.0.x\bin\Exatouch.exe` es del 5 de junio de 2026). Los números de línea solo valen para esa instantánea; hay que volver a anclarlos después de la siguiente fusión. Este documento vive en el repositorio `qa-ai` (commit `020c6cacb76b` en el momento de escribirlo). Todo lo que aparece aquí procede de las lecturas parciales del código (SaveOrder, btnProcessClick A/B, preparación de las formas de pago, totales, los demás manejadores del register, frmRefund, csPayment, Managers/Dapper, escritores satélite, la infraestructura de acceso a la base de datos), del volcado del esquema real (`db-schema.txt`) y del volcado de procedimientos almacenados (`db-procs.txt`). Cuando dos lecturas se contradecían, se abrió el código y se cita la línea que zanja la cuestión. Ningún test modifica jamás nada de lo que cuelga de `C:\Dev\Exatouch`.

**Convenciones.** Las rutas cuelgan de `C:\Dev\Exatouch\2.0.x\`; un número de línea suelto se refiere a `Transactions\frmRegister.cs`. Nombres cortos: `frmRefund.cs` = `Transactions\frmRefund.cs`, `csPayment.cs` = `Library\csPayment.cs`, `Lib.cs` = `Library\Lib.cs`, `OrderManager.cs` = `Managers\OrderManager.cs`, `frmGetOrders.cs` = `Transactions\SubForms\frmGetOrders.cs`, `frmReView.cs`/`frmComplete.cs`/`frmOfflineProcess.cs` = `Transactions\SubForms\`, `frmDelManager.cs` = `Transactions\Delivery\frmDelManager.cs`, `csObject.cs` = `Controls\csObject.cs`, `csDualPriceManager.cs` = `Library\csDualPriceManager.cs`, `GenericRepository.cs` = `Exatouch.Repositories\Common\GenericRepository.cs`. Los enteros de los enum se escriben en línea (`Completed=1`); las tablas completas están en la sección 2. "raw" = `SqlCommand.ExecuteNonQuery/ExecuteScalar/ExecuteReader` (una `SqlException` se propaga fuera del manejador del clic); "wrapper" = las extensiones `ExecNonQuery/ExecScalar/ExecReader` (Lib.cs:3517-3640; diálogo modal de error y, a continuación, se devuelve `null` y el llamador sigue adelante). "batch" = varias sentencias en un mismo `CommandText`: un único viaje de ida y vuelta, **no** una única transacción.

**Advertencia de compilación que cambia las expectativas.** El `Exatouch.csproj` versionado tiene una sola configuración, con `DefineConstants=DEBUG;CFD` (csproj:30-38; todas las configuraciones de la solución apuntan a ella, Exatouch.sln:27-35). En ese binario: el procesador de tarjetas está simulado (`_OnProcess` fabrica el PAN, `CardHolder='Fake credit card'`, `ExpDate='0130'`, un `AuthCode` GUID de 6 caracteres, un `TransmitID` aleatorio de 5 dígitos, rechazo cuando `Millisecond%33==0`, aprobación parcial del 90 % cuando `%27==0`, csPayment.cs:1447-1495; `TxRespons` se escribe como NULL, csPayment.cs:87-91). Dos consecuencias que añade la rama simulada (csPayment.cs:1465-1488): se sitúa al principio de `_OnProcess` sin ninguna guarda de `TxType`, de modo que **las anulaciones, los reembolsos (Return), las capturas y el complete-auth también fallan 1 de cada 33 veces** con `ResponseMessage='Card Declined.'` — justo el mensaje no vacío que hace que frmRefund.cs:2255 lance y que `OrderManager.VoidOrder` haga rollback; y, cuando `Account.TenderFee.Surcharge` está activo, cada aprobación de Credit fabrica `Surcharge=Round(AuthAmount×0.03,2)`, con lo que `CCTxs.Surcharge`, `Payments.Surcharge`, `Orders.Surcharge` y `Orders.Total` se desplazan un 3 % en las ventas con crédito y se ejecuta la rama de recargo de Tab Complete (17061); `SecurityManager.Login` devuelve true incondicionalmente, así que no hay controles de PIN de manager ni filas "Manager PIN Request" en `Log` (Managers\SecurityManager.cs:16-17); `Global.MgrID = Global.UserID = 1` al arrancar (Main\frmMain.cs:2267-2269), por lo que las filas "Manager" de `OrdersLog` llevan `ID=1` en lugar de NULL; `Lib.OpenCashDrawer` no hace nada (Lib.cs:5772); Batch Close ejecuta siempre el SQL de Training (csPayment.cs:159-198). Un binario compilado con una redefinición externa de `/p:DefineConstants` se comporta de otra manera en todos los puntos que dependen del procesador (pregunta abierta 1).

## 1. Panorama general

### 1.1 Dos estilos de persistencia

**Legacy (dominante).** Los formularios construyen a mano el texto de `SqlCommand` y lo ejecutan sobre la única conexión compartida `Global.SqlConnection` (csGlobal.cs:296, getter 309-382). El getter construye la conexión de forma diferida a partir de `lpc:<MachName>\SQLEXPRESS`, catálogo `XTTDB` (o `XTTDBTrain` en máquinas Training), Integrated Security y `MultipleActiveResultSets=True` (csGlobal.cs:254-259), y en **cada** acceso lanza un ping `SELECT 1` con 15 s de timeout, repitiendo `Close()/Open()` detrás de diálogos modales cuando eso falla (340-376). Un grep de `BeginTransaction|TransactionScope|SqlTransaction|BEGIN TRAN|XACT_ABORT` sobre todo el árbol encuentra exactamente una coincidencia, `Data\XTUnitOfWork.cs:43`, y sobre una conexión distinta. **Por lo tanto, toda sentencia legacy hace autocommit.** Los CommandText de varias sentencias (decenas de ellos, por ejemplo el commit por forma de pago 17938-17963 y el lote de finalización 18622-18644) aportan orden y un único viaje de ida y vuelta; cada sentencia interna sigue haciendo commit por su cuenta y `XACT_ABORT` nunca se activa, de modo que un fallo en la sentencia N deja confirmadas las 1..N-1 y, si el error es de nivel de sentencia, todavía ejecuta la N+1.

**Nuevo (acotado).** Las clases estáticas `Managers\*` abren un `XTUnitOfWork` y escriben a través del `GenericRepository<T,K>` de Dapper: `Add` emite `INSERT INTO <table> (<all non-key columns>) VALUES (...); SELECT SCOPE_IDENTITY();` (GenericRepository.cs:33-35); `Update` es un `UPDATE <table> SET <every non-key property> WHERE key=@key` de **fila completa** (115, 170-194) que reescribe cada columna con el valor leído momentos antes (las columnas que no son propiedades de la entidad — `Orders.SplitPayments`, `Orders.Surcharge`, `CCTxs.Surcharge` — no se tocan nunca); `UpdateFields` es un SET parcial (133-143); `Delete` lanza NotImplemented (148-151). `new XTUnitOfWork(true)` abre su **propia** `SqlConnection` y llama a `BeginTransaction()` (XTUnitOfWork.cs:39-43); el `Dispose()` de una unidad sin commit hace rollback (137-150). `new XTUnitOfWork()` sin ese flag no tiene transacción. Solo `OrderManager.VoidOrder` (OrderManager.cs:64), `AccountPaymentManager.TakeAccountPayment` (AccountPaymentManager.cs:34) y el `AccountPaymentManager.UpdateAccountPaymentRate` de una sola sentencia (103-111) usan la forma transaccional; `PaymentManager.UpdateTip`, `GiftCardManager.*`, `CardTransactionManager.UpdateNumber`, `OrderManager.RequestRemoteReprint` y `AuditManager` hacen autocommit sobre su propia conexión. Como esa conexión está separada de `Global.SqlConnection`, una escritura de Dapper dentro de la transacción de anulación mantiene bloqueos X que bloquean a los lectores legacy de esas mismas filas (`read_committed_snapshot` OFF; 30 s de command timeout).

### 1.2 Comportamiento ante errores de una sentencia

- Las llamadas **wrapper** (`ExecNonQuery/ExecScalar/ExecReader`, Lib.cs:3517-3640) capturan `SqlException` y llaman a `_SqlException` (Lib.cs:8925-8953), que es **modal**: "Error executing SQL Script … Retry?" — Retry **no** vuelve a ejecutar (el `break` se ejecuta igualmente, Lib.cs:3532-3536); Cancel escribe `log.txt` y pregunta "Ignore?" — OK devuelve `null` al llamador, y si no `Global.MainForm.Restart=true; Start.Exit()`. Los llamadores rara vez comprueban ese null: un `OrderID`/`PayID`/`ODID` nulo lanza entonces `NullReferenceException` en la línea siguiente (2049, 2440, 2607, 2727) y aparece un segundo modal "Please contact Exatouch support" (Lib.Error vía Start.cs:233-253). Una `InvalidOperationException` es el único caso que vuelve a ejecutar el comando: `_SqlConnException` (Lib.cs:8909-8923) reasigna el comando a `Global.SqlConnection` y entra en bucle; cuando la conexión está Closed/Broken cada pasada muestra un modal "Connection to the server has been lost. Would you like to Retry?" (Cancel → "Ignore?" → resultado null, o `Start.Exit()`), y cuando la conexión sigue Open (por ejemplo, un conflicto de MARS/DataReader) el bucle es silencioso e ilimitado.
- Las llamadas **raw** lanzan. frmRegister.cs tiene 78 puntos raw con `ExecuteNonQuery` frente a 8 envueltos; frmRefund.cs, 16 frente a 1. `SaveOrder` (1748-2817) y `btnProcessClick` (16277-19115) no tienen try/catch, así que una excepción aborta el manejador a mitad de secuencia con todo lo anterior ya confirmado.
- La consecuencia para la automatización de interfaz: cualquier fallo de base de datos produce uno o dos diálogos modales que hay que cerrar, y el residuo es un prefijo de la lista de escrituras, nunca un rollback (sección 11).

### 1.3 Propiedades del esquema que importan

XTTDB **no tiene claves foráneas** (`sys.foreign_keys` vacía), **no tiene triggers** (`sys.triggers` vacía) y **no tiene restricciones CHECK** en las tablas de pedidos; solo hay PK agrupadas e índices no agrupados simples (Orders: BatchID, CustomerID, StaffID, TerminalID; Payments: BankID, CCTxID, OrderID, TerminalID, UserID; OrdersDetails: CatID, ItemID, OrderID, ServiceID, StaffID, SubCatID, UserID; OrdersTips: OrderID, PayID, StaffID). Toda columna que no sea clave admite NULL y carece de valor por defecto, salvo `CDate` (`getdate()`) y `Active` (`1`) en Bank/FloorPlan/Staff. Los huérfanos son posibles y nada se propaga en cascada: `Payments.OrderID=-OrderID` (borrado lógico, 2564, 18996) es un estado normal, `FloorPlan.OrderID` puede apuntar a un pedido que se unió a otro (ruta Lib.cs:5013) y borrar una mesa del plano de salón deja `Orders.FPID=NULL` en todo el histórico (Integrations\FloorPlan\frmSetFloorMap.cs:620).

### 1.4 Valores de identidad y cómo vuelven al formulario

Las claves vuelven de tres formas: `OUTPUT INSERTED.[col]` leído con `ExecuteScalar` (primera columna, primera fila) — la norma en frmRegister/frmRefund; `SELECT SCOPE_IDENTITY();` añadido a un lote (hijo de la división 19619, pedido de reembolso frmRefund.cs:2450, todo `Add` de Dapper); `@@IDENTITY` en los procedimientos almacenados (db-procs.txt:60, 65) y en el SQL de fichaje (Lib.cs:770) — seguro solo porque no existe ningún trigger.

| Clave | Producida por | Aterriza en | Se usa después para |
|---|---|---|---|
| `Orders.OrderID` | `INSERT INTO [Orders] … OUTPUT INSERTED.[OrderID]` 2030-2032, `MainID = mSqlCmd.ExecScalar()` 2045 | `frmRegister.MainID` (DBNull en un carrito nunca guardado; `mIsNew = MainID.Equals(DBNull.Value)` 1750) | todo `WHERE [OrderID]=@OrderID` posterior, el número de pedido en pantalla (2047-2049), la liberación del bloqueo en `Clear()` (739) |
| `OrdersDetails.ODID` | `INSERT INTO [OrdersDetails] … OUTPUT INSERTED.[ODID]` 2425-2440 | la columna `ODID` del DataRow `_OrderDetails` | insert o update en el siguiente guardado (2371-2374 vs 2455), borrados encolados `csDelRow(ODID, ItemID)` (13542-13545), `OrdersTimeCharge.ODID` (2447) |
| `Payments.PayID` | `INSERT INTO [Payments] … OUTPUT INSERTED.[PayID]` 2595-2607 | la columna `PayID` del DataRow `_Payments` (DBNull mientras está en preparación) | `CardData.PayID` (17124), para que `csPayment.SaveTrx` pueda enlazar `CCTxs` (csPayment.cs:108-118); todo `WHERE [PayID]=@PayID` del bucle de formas de pago (17972); `Orders.TabAuthID` (17490); eliminación o borrado lógico (19362-19365, 2558-2569) |
| `CCTxs.CCTxID` | `INSERT INTO [CCTxs] … OUTPUT INSERTED.[CCTxID]` csPayment.cs:93-104 | `CardData.CCTxID` y luego `Payments.CCTxID` mediante `UPDATE [Payments] SET [CCTxID]=@CCTxID, [Reference]=@AuthCode WHERE [PayID]=@PayID` (csPayment.cs:112-118, se omite cuando `PayID` es DBNull) — batch A (17938) reescribe después `Reference` con el valor de la fila en preparación, de modo que el `Reference` de una fila de tarjeta acaba en **NULL** tras el flujo de pago (el AuthCode solo sobrevive en `CCTxs.AuthCode`; las filas de Tab Auth retornan antes de batch A y lo conservan) | anulaciones, capturas y reembolsos unen `Payments` con `CCTxs` por él |
| `OrdersTips.TipID` | `OUTPUT INSERTED.[TipID]` 2724-2727, 16794, 17554 | fila de `_Tips` / `mValues.Object4` | update/delete por TipID (2702, 2733), negación de WithError (19040) |
| `OrdersTax.OTID` | `OUTPUT INSERTED.[OTID]` 2640-2644 | lista `_Taxes` | reutilización FIFO al volver a guardar (2649-2654), sobrantes puestos a NULL (2671-2675) |

`Global.TerminalID` es la fila de `Terminals` correspondiente a esta MAC (`UPDATE [Terminals] SET [Name]=@Name OUTPUT INSERTED.[TerminalID] WHERE [MAC]=@MAC`, frmMain.cs:1927; se inserta cuando es null, 1939-1943) y se enlaza tanto a `Orders.TerminalID` como a `Orders.InUseID` en el insert (2032-2034: el mismo parámetro `@InUseID` alimenta ambas columnas) y a `Orders.InUseID` al recuperar el pedido (8922-8925). `Global.UserID` procede de `Lib.CheckPIN` (Lib.cs:1037); `Global.MgrID` lo fijan las autorizaciones de manager y **nunca se limpia** (Lib.cs:1072, 1133; por defecto `DBNull.Value`, csGlobal.cs:170). `Global.LocalSetting.BankID` vive en `Exatouch.cfg`, no en la base de datos, y es lo que se enlaza en cada `Payments.BankID` (`?? DBNull.Value`: 2598, 16161, 17971, 18340; frmRefund.cs:1812, 2693, 2778; frmDelManager.cs:630).

### 1.5 Hilos y la conexión compartida

La finalización ejecuta su lote de cierre en un **hilo nuevo** (18620) sobre la conexión MARS compartida mientras el hilo de interfaz ejecuta `Clear()` → `UPDATE [Orders] SET [InUseID]=NULL` (739). Por eso las aserciones deben sondear `Orders.Status=1 AND CloseDate IS NOT NULL` en lugar de leer inmediatamente después del clic. `AuditManager` (`Log`) usa siempre una segunda conexión (AuditManager.cs:190). La impresión se ejecuta en hilos desacoplados salvo `OrderDetailsAsync` que, pese a su nombre, se bloquea en `WaitOne` (Reports\csPrint.cs:3071-3085), de modo que su actualización de `OrdersDetails.Printed/Fire` termina antes de que `SaveOrder` retorne.

## 2. Guía rápida del modelo de datos

Las listas de columnas provienen de `db-schema.txt` (Orders, OrdersDetails, Payments, CCTxs, OrdersTips, OrdersTax, OrdersDelivery, OrdersFPSplit, FloorPlan, GiftCards, Terminals, AccountPayments). Las tablas que **no** están en el volcado (OrdersLog, Items, Bank/BankMod/BankDetails, CustomFields, Log, OpenPromoDetails/OrdersPromoDetails, CCTxFailures, Staff, Customers, Config) se describen únicamente a partir del texto SQL (pregunta abierta 2).

### 2.1 Orders (OrderID de identidad, CDate por defecto getdate())

| Columna | Quién la escribe / significado |
|---|---|
| `Status` smallint | `OrderStatus` (2.4). Open=0 al insertar (2030, @Status 1858); Pending=6 por cada forma de pago (17979); Completed=1 al cerrar (18651); nunca aparece en la lista SET del re-guardado (2093) |
| `Paid` money | `IIF(@NoTender=1,NULL,0)` al insertar (2032; `@NoTender=_Payments.DefaultView.Count==0` 1982); `+=Amount` por cada forma de pago (17960); `+=Tip` en `csPayment.Complete` (csPayment.cs:502); `-=Amount` en `VoidAuth` cuando `pVoidOrder=false` (1138); `-=_Ref` en los reembolsos Pending (frmRefund.cs:2725) |
| `Total`, `Tax`, `ItemTotal`, `BaseAmount`, `TDUsed`, `TFUsed`, `Surcharge`, `OtherAmount`, `DeliveryFee`, `Tip`, `Credit`, `Exchange`, `Loyalty` | totales del formulario en cada `SaveOrder` (2030/2093); `Total += tip` (16794, 17554, csPayment.cs:502, frmReView.cs:160); `Tip` se reescribe con `_TipAmt` al cerrar (18652); `Surcharge` = SUM(Payments.Surcharge) (17961, 18666, csPayment.cs:527) |
| `InUseID` int | bloqueo de uso: `Global.TerminalID` al insertar (2034) y al recuperar el pedido (8925); NULL en `Clear()`/al cerrar el formulario (739, 2969) y en frmRefund `_Clear`/Cancel (frmRefund.cs:222, 1571); la carga de frmRefund lo pone a `Global.TerminalID` (725) |
| `TerminalID`, `StaffID`, `TaxID`, `CustomerID`, `CAID`, `CustomID`, `OtherID`, `FPID` | inserción (2030); el re-guardado reescribe todas menos `StaffID`/`TerminalID` (2093); `StaffID` lo cambian Reassign/Take (19234/19246, 14802/14808), el pedido origen al unir (Lib.cs:5013) y el repartidor (Lib.cs:6947) |
| `CloseDate` | lote de cierre (18639), Pay In/Out (16135), anulación desde la cuadrícula (frmGetOrders.cs:429), pedido origen al unir (Lib.cs:5013), pedidos de reembolso y de OpenRefund (frmRefund.cs:2448, 1808), cierre de efectivo de reparto (frmDelManager.cs:606); se vuelve a poner a NULL ante un rechazo offline (frmOfflineProcess.cs:200) |
| `BatchID` | literal `0` al cerrar, NULL cuando existe un artículo con código de impuesto (18647); NULL en el cierre de reparto; nunca la sesión de cajón (bank) (la integración LMP la escribe para informes, Integrations\LMP\csClassLMP.cs 879/1060/1185 — sin trazar) |
| `TabAuthID`, `TabLimit`, `TabMin`, `TabAlert`, `Label` | Tab Auth (17486-17501); `TabAuthID=NULL` al completar (17085) y al anular la fila del tab (csPayment.cs:1146) |
| `RefundAmt`, `VoidDate`, `RefID`, `ReasonID`, `Note` | `RefundAmt += _Ref` / `=Paid` al reembolsar o anular (frmRefund.cs:2706-2725, OrderManager.cs:168); `VoidDate` (frmRefund.cs:2725 anulación, OrderManager.cs:169); `RefID` = el pedido original en los pedidos de reembolso (frmRefund.cs:2448), = el pedido destino en los orígenes de una unión (Lib.cs:5013), = `CustomFields.CustomID` en Pay In/Out (16135); `ReasonID` anulación desde la cuadrícula (frmGetOrders.cs:429), pedidos de reembolso (frmRefund.cs:2448, 1808) |
| `Printed` | 0 al insertar y en cada re-guardado (2032, 2113); 1 desde `_PrintOrder` (9933) y desde el temporizador de pedidos online (frmMain.cs:3321) |
| `Source`, `OrderType`, `LegalAge`, `LegalAgeMethod`, `LicenseName`, `PhoneNumber`, `SplitPayments`, `CustExempt` | inserción (2030); `OrderType` también en el re-guardado (2093) |
| `PaidAtTable` | solo `spApplyPayment` (db-procs.txt:71) |
| `RequestRemoteReprint` | Dapper 1/0 (OrderManager.cs:37/243) |
| `Rate` | valoración por estrellas del CFD (SysForms\CFD\frmCFD.cs:632) |
| `OtherRefunded`, `ConvFee`, `OnlineOrder`, `OOTime`, `RemoteStatus`, `CashDiscEnabled`, `InUse`, `Sent`, `MachName` | solo se leen en el código trazado (`OtherRefunded` se lee en frmRefund.cs:323; se desconoce quién lo escribe, pregunta abierta) |

### 2.2 OrdersDetails (ODID de identidad, CDate por defecto)

Filas de línea. `OrderID`, `ItemID` (pseudo-artículos reservados: Refund=1, Certificate=5, GiftCard=6, Club=7, CommentPrice=8, ShareItem=9, Comment=10, ShareService=11, Course=14, Seat=15, NotPrinted=19), `Qty`, `QtyDel` (cantidad retirada acumulada), `RefQty` (cantidad reembolsada acumulada), `Deleted` (borrado lógico), `DelUserID`, `UserID` (quien la creó, `Global.UserID` 2387), `StaffID` (el camarero), `LinePos` (índice en la cuadrícula al guardar, 2342), `Price` (neto del impuesto incluido), `PriceBase`/`PriceDisplay`/`PriceOriginal` (precio de tarifa), `PriceSell` (neto de todos los descuentos, 11865), `PriceTax`/`PriceSinTax` (impuesto por unidad), `Printed` (**money**: `=Qty` cuando se imprime en remoto, csPrint.cs:1922; `=-Qty` en el borrado lógico, 2530), `Fire` (bit), `Modifier`/`ModHas`/`ModRollup`, `PromotionID`/`DiscType`/`DiscUnit`, `PromoCode`/`PromoPrice`/`PromoGrpID`/`PromoLink`/`PromoQty`/`PromoUnit`/`PromoUnitDisc`, `SDID`/`LoyaltyUnit`/`LoyaltyDisc`/`ScanData`, `TaxableA-D`/`TaxExempt`/`TaxType`/`TaxRatio`/`TaxIncluded`, `ToGo`, `Shared` (filas de las partes al dividir), `SKU`/`ScannedSKU`, `Unit` (HR/MN/SC → OrdersTimeCharge), `DepartmentID`. No existe ninguna columna `Exchange`: las líneas de cambio son `Qty<0 AND Ratio IS NULL` (8691).

### 2.3 Payments, CCTxs, OrdersTips, OrdersLog, OrdersTax, OrdersDelivery, OrdersFPSplit, FloorPlan, Bank, Items

**Payments** (PayID de identidad): `OrderID` (negativo = borrado lógico, 2564/18996), `Type` = `PayType`, `Status` = `PayStatus`, `Amount` (importe aplicado), `Value` (efectivo entregado / saldo restante de la tarjeta de regalo / importe autorizado del tab / puntos de fidelización), `Tip` (parte de la propina del pedido asignada a esta forma de pago, 17994-18017), `TDUsed`/`TFUsed`/`Surcharge`/`FeeFromTips` (descuento por forma de pago, comisión por forma de pago y recargo), `DeliveryFee` (parte del repartidor), `CCTxID`, `Reference` (número de cheque, nota del pay-out; en las filas de tarjeta `SaveTrx` escribe el AuthCode, pero batch A lo sobrescribe con el valor preparado, NULL — solo las filas de Tab Auth lo conservan), `RefID` (PromotionID del cupón, CustomID del motivo del pay-out), `RefundAmt`, `BankID`, `TerminalID`, `UserID`, `CloseUserID`, `CloseDate`, `MachName` (solo `'Clover Flex'`, desde `spApplyPayment`), `FromSplit` (nunca se escribe en el código trazado).

**CCTxs** (CCTxID de identidad): `TerminalID`, `TID`, `TransmitID`, `AType` (AccountType), `AuthCode`, `PNRef`, `CardType`, `CardHolder`, `CCNumber` (8 caracteres, `'****dddd'`), `ExpDate`, `Number` (varbinary, PAN cifrado con RSA, solo offline/eGift), `Sign` (image), `Source` (MANUAL=0, SWIPE=1, EMV=6), `Status` = `CCStatus`, `TxType`, `Tip`, `Surcharge`, `Partial`, `RAmt` (importe anulado o reembolsado), `OffLineStatus`, `BatchClose`, `CloseDate`, `TxRespons`, `BatchID` (2.0.x nunca la escribe). **No hay ninguna columna de importe**; el importe vive en `Payments`.

**OrdersTips** (TipID): `OrderID`, `PayID` (NULL en las propinas Group/Amount/Percent a nivel de pedido), `PayOutID`, `StaffID`, `Amount`, `Value` (fracción de porcentaje o importe), `Type` = `TipType`, `RefAmount` (se establece al reembolsar, frmRefund.cs:2530).

**OrdersLog** (OLID de identidad, CDate por defecto; no está en el volcado): `OrderID`, `ID` = un `Staff.StaffID` (los informes hacen JOIN por ella, Reports\csSQL.cs:43, 860), `Type` = `OrderLogType`. Solo se INSERTa; nunca se actualiza ni se borra.

**OrdersTax** (OTID): una fila por cada ranura de impuesto `Type` 0..29 con `Qty` (base imponible) y `QtyExempt`; los Type 0-3 siempre existen en un pedido guardado (2626-2644); las filas sobrantes se ponen a NULL (`Qty/QtyExempt/Type=NULL`), nunca se borran (2671). **OrdersDelivery** (ODELID): `3PDSID`, `OrderID`, `StaffID` (el repartidor), `Status` = `DeliveryStatus`, marcas de tiempo `Ready/Out/Delivered`, `DeliveryFee`, `DeliveryFeeDollar` (bit), `DeliveryFeeTotal` (**bit** — SaveOrder enlaza un importe money, que se almacena como 1 cuando no es cero, 2194), `RefNumber`. **OrdersFPSplit** (FPSID): `FPID`, `OrderID`, `Status` (FPStatus) — una fila por cada subpedido que comparte una mesa. **FloorPlan** (FPID): `OrderID`, `InUse`, `Status` = `FPStatus`, `LastAct` (se sella con `GETDATE()` cuando se escribe `InUse` a 0, csObject.cs:469-479), `Label`, `Active`. **GiftCards** (GiftID): `GiftCode`, `Amt`, `Balance`, `Used`, `UsedDate`, `DateSold`, `FromRefund`, `CustomerID` (0, no NULL, para clientes sin ficha creados desde el register, Lib.cs:7351-7360), `OrderID`. **Bank** (`Status`: Open=0, SwapIn=1, SwapOut=2, Closed=3; `StaffID`, `MngrID`, `TerminalID`, `ShiftBegin/End`, `CashOpen`, `Delta`, `PayOutIn`, `RefCash`), **BankMod** (`BankID`, `UserID`, `MngrID`, `Cash` con signo, `Date`, `Notes`), **BankDetails** (`BankID`, `Type`=PayType, `Amount` contado, `Expected`). Stock en **Items**: `QtyOnHand`, `QtySold`, `StockType` (Regular=0, NotInventory=2), `AutoMoveBulk`, `Alert`, `Active`. **Terminals**: `TerminalID`, `MAC`, `Name`, `AuthDate`/`Sent` (primera aprobación de crédito online), `OffDate`/`OffSent` (transacciones offline pendientes).

### 2.4 Valores de los enums (enteros tal como se almacenan)

| Enum (origen) | Valores |
|---|---|
| `OrderStatus` (Library\csEnums.cs:854) → `Orders.Status` | None=-1, Open=0, Completed=1, JoinIn=2, Deleted=3, Refunded=4, Voided=5, Pending=6, RefPending=7, VoidPending=8, RefProcessing=9, OpenRefund=10, Undeliverable=11. `dbo.OrderStatus()` solo mapea de -1 a 7 (db-procs.txt:4). En el código trazado no se encontró nada que escriba 3, 7, 8 ni 9 |
| `PayStatus` short (csEnums.cs:912) → `Payments.Status` | Open=0, Completed=1 ('Paid'), RemoteQueue=2, RemoteProgress=3, RemoteError=5, Auth=6, Refunded=8, Voided=9, TabAuth=11, OpenRefunded=13, Deleted=14. `dbo.PayStatus()` nombra además 4 Pending, 7 Partial, 10 Ref Pending y 12 Void Pending (db-procs.txt:8), que el enum de C# no tiene |
| `PayType` short (Exatouch.Entities\Enums\PayType.cs) → `Payments.Type` | NoTender=-1, Cash=0, Check=1, Coupon=2, Credit=3, Debit=4, Discount=5, EBTFood=6, eGift=7, PayIn=8, PayOut=9, Reward=10, Offline=11, Certificate=12, PayOutCust=13, CheckCashBack=14, ClubDiscount=15, DeliveryService=16, Factor4Loyalty=17, EBTCash=18, OnAccount=19, CardOnFile=20 (se almacena como 3, 2583). `dbo.PayType()` se detiene en 19 |
| `OrderType` (csEnums.cs:871) → `Orders.OrderType` | Regular=0, TakeOut=1, PickUp=2, Delivery=3, Custom=4, DeliveryService=5, PayIn=6, PayOut=7 |
| `CCStatus` short (csEnums.cs:537) → `CCTxs.Status` | Authorized=0, Completed=1, TabAuth=2, Voided=3 (`spApplyPayment` escribe además un 6, que no es miembro del enum) |
| `TxType` (XTPE2, `dbo.CCTxType` db-procs.txt:2) → `CCTxs.TxType` | Auth=0, Capture=1, Sale=2, Force=3, Adjustment=4, Return=5, Void=6, Inquiry=7 |
| `AccountType` (XTPE2\Enums.cs) → `CCTxs.AType` | Debit=0, Credit=2, EBTCash=3, GiftCard=4, EBTFood=5, Offline=6, CardOnFile=7 (Lib.cs:9073) |
| `OffLineStatus` (csEnums.cs:445) → `CCTxs.OffLineStatus` | None=0, Failed=1, Declined=2, Approved=3, Voided=4 |
| `OrderLogType` (csEnums.cs:834-843) → `OrdersLog.Type` | Create=0, Close=1, Manager=2, PrevOwner=3, Force=4, ApprovedBy=5, Voided=6 |
| `TipType` short → `OrdersTips.Type` | None=0, CC=1, Group=2, Amount=3, Percent=4, Terminal=5, Gift=6, CFD=7 |
| `FPStatus` (csEnums.cs:650) → `FloorPlan.Status`, `OrdersFPSplit.Status` y códigos de socket | None=0, Open=1, Busy=2, Ordered=3, Check=4, Authorized=5, CleanUp=6, TabAuth=7, Pending=8, Error=9, Split=10; comandos del menú emergente Complete=50, Fire=51, Join=52, Move=53, Reassign=54, Take=55, TipAdd=56, TipDel=57, Print=59, FullCash=60, FullCredit=61, FullDebit=62 |
| `DeliveryStatus` short (csEnums.cs:563) → `OrdersDelivery.Status` | New=0, Ready=1, Out=2, Delivered=3, Undeliverable=4; `StaffDeliveryStatus` (csEnums.cs:1063) Available=0, Assigned=1, Out=2 |
| `DiscType` (csEnums.cs:591) → `OrdersDetails.DiscType` | TenderDiscount=1, Club=2, DollarAmount=3, Global=4, MfgGlobal=5, MfgSingle=6, Override=7, Single=8, Promo=9, Reward=10, ModCredit=11, ScanData=12 |
| `StockType` (csEnums.cs:465) → `Items.StockType` | Regular=0, NotInventory=2 |
| `MachineType` (csEnums.cs:348-358) | StandAlone=0, Server=1, Sidekick=2, Training=3 (Training usa `XTTDBTrain`) |
| `CustomType` → `CustomFields.Type` | RefundReason=14, PayIn=16, PayOut=17, PayOutCustomer=18; ids de motivo por defecto: 39 reembolso, 40 anulación, 43 cliente de pay-out |

## 3. Creación de un pedido nuevo: `SaveOrder` paso a paso

`SaveOrder(object sender)` (1748-2816) es la **única** rutina que persiste un pedido del Register — para un carrito nuevo hace INSERT, para uno recuperado hace UPDATE, y en ambos casos recorre líneas, pagos, impuestos, promociones y propinas. Nada más en el Register crea una fila de `Orders` salvo Pay In/Out (16135), los hijos de Split (19615) y la pantalla de reembolso (frmRefund.cs:1808, 2448, 2961).

### 3.1 Quién lo llama (el `sender` importa)

| Acción de IU | Punto de llamada | `sender` | En torno al guardado |
|---|---|---|---|
| **Save** (en espera), Print Order, Print Remote, To-Go Print | btnSaveClick 19399 | el botón | Solo se ejecuta cuando `Modified \|\| (RemoteOption.Firing && (_CmdFire.Selected \|\| sender==_CmdPrintRemote))` (19392-19396); un Save sin nada modificado → "Nothing changed." (19417). Si tiene éxito, `_DefaultScreen(1)` → `Clear()` → `UPDATE [Orders] SET [InUseID]=NULL` (19407 → 6922-6923 → 739-743). Resultado: un pedido en espera es simplemente `Status=0, InUseID=NULL, Paid=NULL` — **no existe ningún estado "held"** |
| Add Tip | btnAddTipClick 13168 → btnSaveClick | `_CmdAddTip` | sin `Clear()`; se imprime el TipSlip (19438-19439); el bloqueo se mantiene |
| **Pay / Process** | btnProcessClick 16637 | botón de forma de pago (`_TndCash1`, `_TndCredit1`, …) | `if (Modified)`; el pedido **y sus filas de forma de pago preparadas** se persisten **antes** de invocar al procesador; también 16371 (`_CmdSave`, primer registro de tab-auth), 16530 (`_TndTabComplete1`, tab en poder de otro terminal, luego `Clear()` 16543 y salida), 17423 (cancelación de aprobación parcial con TD/TF) |
| Salir / recuperar | `_CheckForSave` 6397, 6411 | `_CmdSave` | AutoSave → guardar + `Clear()`; si no, "Save Changes?" — frmMessageBox asigna el **Yes** visible→`DialogResult.OK` (guardar + `Clear()`), **No**→`Cancel` (continuar **sin** guardar), **Cancel**/X→`No` (abortar) (SysForms\frmMessageBox.cs:130-143) |
| Fire Now | btnFireNowClick 13972 | el botón | primero `_CmdFire.Selected=true`; `Clear()` justo después (13973) |
| Recuperar con Join | btnGetOrderClick 14054 | botón Get Order | solo cuando frmGetOrders lanzó `OnJoinOrder`; después `UPDATE [Orders] SET [Tax],[Total]` (14059-14066) |
| Aplicar/quitar propina de grupo | btnPercentClick 16267 | `_CmdPercent` | fila de `_Tips` con `Type=Group=2`, `Value=GroupTip/100` (16243-16249) o `Deleted`; solo guarda un pedido existente (16265) |
| Split | btnSplitClick 19597 | `_CmdSplit` | guarda el origen antes del INSERT del hijo (19615) |
| Check Out de una cita | CheckOut 714 | `true` boxeado | después `UPDATE [Appointments] SET [OrderID],[Completed]=1` por cada cita (717-727) |

**Por qué Pay siempre guarda un pedido recuperado.** reg-process-a interpretó que `Modified` nunca se activa al pulsar una forma de pago; reg-save sostenía que lo activa la cuadrícula. El código lo zanja: `_AddPayment` añade la fila preparada a `_Payments` (4595-4602); `dgvRC.DataSource=_Payments` (1640); `csDataGridView.OnRowsAdded` pone `FormParent.Modified=true` cuando `IsHandleCreated && AllowUpdate && FormParent!=null && !IsNewRow` (Controls\csDataGridView.cs:927-933); `AllowUpdate` vale true por defecto (csDataGridView.cs:82) y `csForm` asigna `FormParent=this` a cada `csDataGridView` de su árbol (Controls\csForm.cs:824); el setter de `Modified` es una simple bandera protegida por `Global.IsRunTime` y `!_Paused` (csForm.cs:104-137). `dgvRC` está sobre `tpgPayments` (frmRegister.Designer.cs:2793), así que su handle existe en cuanto se ha mostrado la pestaña Payments — es decir, en cada pulsación normal de una forma de pago. Salvedad pendiente: una ruta de pago que nunca muestre esa pestaña durante la sesión (el emergente del plano de salón FullCash=60/FullCredit=61/FullDebit=62) podría dejar `IsHandleCreated` en false; verifícalo empíricamente antes de hacer aserciones sobre filas indexadas por PayID en ese caso.

### 3.2 Validación (1755-1913) y las tres escrituras que esconde

Precondiciones: filas de detalle o un importe de paid/PayOutCust (1757-1767); `_Total >= _Paid` (1769-1778); `_Total <= 99,999,999.99` (1780-1784); diálogo de etiqueta cuando `Register.Order.LabelOnOrder` y no hay etiqueta ni cliente (1787-1808; Cancel → `_ResetPay()` y `return false`); excepción al mínimo de pedido en Delivery propio (1810-1824) **else-if** búsqueda de cliente por `Register.Order.ReqCustomer` (1825-1830); para un pedido existente, `SELECT [InUseID] FROM [Orders]` debe coincidir con `Global.TerminalID` (1846-1854), y si no "Order may have been modified on another terminal, cannot save."; comprobación del límite de crédito del cliente sobre sus pedidos con `Status=0` (1860-1887), con un posible inicio de sesión de manager; las compilaciones Release se niegan pasada la fecha de licencia o de prueba (1906-1910); las compilaciones DEMO reinician todas las transacciones a los 100 pedidos (1889-1904, `Lib.ResetTrxs` Lib.cs:6345-6444 — hace TRUNCATE de todas las tablas de pedidos).

| # | archivo:línea | tabla | escritura | condición |
|---|---|---|---|---|
| V1 | 1773 → 739 | Orders | `InUseID=NULL` y luego `return false` | `Global.IsLock && _Total>=0 && _Total<_Paid`, pedido existente |
| V2 | 1827 → 7785 | Customers | `ClubNumber=@ClubNumber` | la búsqueda de ReqCustomer elige un miembro de fidelización sin número de club |
| V3 | 1878 → SecurityManager.cs:110 → AuditManager.cs:29 | Log | fila "Manager PIN Request" (Dapper, segunda conexión, escrita **antes** del diálogo del PIN, aunque se cancele) | límite de crédito superado, solo en compilación Release |

### 3.3 Banderas y parámetros de cabecera

`mIsNew = MainID.Equals(DBNull.Value)`; `mHasTabAuth = _HasTabAuth || (TabEnabled && sender==_TndTabAuth1)` (1750-1753). Si `_HasTabAuth && Modified`, el `Amount=_GetBalance(PayType.Credit)` y el `TFUsed` de la fila del tab se recalculan en memoria (1925-1954) y los persiste el UPDATE de Payments (2613). Todas las columnas de `Orders` se enlazan desde el estado del formulario (1960-2024); `PhoneNumber` = los dígitos de `Customers.Phone` (2003-2005; si falta la fila del cliente se lanza una excepción antes de cualquier escritura) o los de `_PhoneNumber` en la fidelización por ScanData (2018-2021).

### 3.4 Pedido nuevo: el INSERT (2030-2045)

`INSERT INTO [Orders] (...) OUTPUT INSERTED.[OrderID] VALUES (...)`, `ExecScalar` en modo wrapper → `MainID` (2045; si es null → NRE en 2049).

| Columna | Valor | Origen |
|---|---|---|
| `CAID`, `CustomerID`, `CustomID`, `OtherID` | `_CAID`, `_CustomerID`, `_OTSelected.ID`, `_Other.PromotionID` | 1960, 1857, 1969; se espera que `CustomID` sea NULL en los tipos de pedido integrados (pregunta abierta) |
| `FPID` | `_CurrentObj.ID` solo cuando `License.FloorPlanEnabled` | 1961 |
| `InUseID`, `TerminalID` | **ambas** con `@InUseID` = `Global.TerminalID` | 2032, 2034 |
| `StaffID` | el campo `StaffID` del formulario = `Global.UserID` tras `Clear()` (953) o el `Orders.StaffID` de un pedido recuperado (8589); lo cambian Reassign/Take (19255) | 1963 |
| `TaxID` | `Global.Tax.TaxID` | 1964 |
| `BaseAmount`, `Credit`, `CustExempt`, `Exchange`, `ItemTotal`, `Loyalty`, `Tax`, `Tip`, `Total`, `TDUsed`, `TFUsed`, `SplitPayments` | `_BaseAmount`, `_Credit`, …, `_TipAmt`, `_Total` (fórmulas de la sección 4) | 1971-1999 |
| `DeliveryFee` | `@DeliveryFeeTotal` = `_DeliveryFee` | 1971 |
| `Surcharge` | `_Surcharge`, **NULL cuando vale 0** | 1999 |
| `Label` | `_Label`, salvo que coincida con los dígitos del teléfono → NULL | 2036-2043 |
| `LegalAge`, `LegalAgeMethod`, `LicenseName` | estado de la verificación de edad (Override=1/Manual=3/Scanned=4) | 1975-1977 |
| `Note` | `txtDiscNotes.Text`, NULL si está en blanco | 1980 |
| `OrderType` | `(OrderType)_OTSelected.Tag` | 1984 |
| `OtherAmount` | `_Other.Ratio` | 1986 |
| `Paid` | `IIF(@NoTender=1, NULL, 0)`; `@NoTender = _Payments.DefaultView.Count==0` (filtro de la vista `Deleted=0 AND Status IN (0,1,6)`, 1638) | 1982, 2032 |
| `Printed`, `Source` | literal 0, literal 0 (`OrderSource.Exatouch`) | 2032 |
| `Status` | `@Status` = `OrderStatus.Open=0` | 1858 |
| `TabAlert` | `_TabAlert` (0 tras `Clear()` 960) | 1992 |
| `PhoneNumber` | como se indica arriba | 2001-2024 |
| valores por defecto / NULL | `CDate=getdate()`; `BatchID, CloseDate, RefID, ReasonID, TabAuthID, TabLimit, TabMin, RefundAmt, VoidDate, PaidAtTable, RequestRemoteReprint, Rate, MachName` NULL | esquema |

Justo después: `INSERT INTO [OrdersLog] ([OrderID],[ID],[Type]) VALUES (MainID, @StaffID, 0)` (2052-2058, raw; `ID` = el campo `StaffID`, `Type=OrderLogType.Create=0`); `(MainID, _AgeApprover, ApprovedBy=5)` cuando se registró una aprobación de edad (2060-2068); con el plano de salón licenciado y una mesa seleccionada, `_CurrentObj.Status=FPStatus.Ordered=3; DBUpdate()` → `UPDATE [FloorPlan] SET [OrderID]=MainID, [InUse]=0, [Status]=3, [LastAct]=GETDATE() WHERE [FPID]` (2070-2084 → csObject.cs:421-423, 472). El setter de status retorna de inmediato si no hay cambio o si `(!EditMode && !ForUse)` (csObject.cs:412); entonces `DBUpdate` reescribe el estado **antiguo** y `InUse` (pregunta abierta).

### 3.5 Pedido existente: el UPDATE (2087-2155)

Un único CommandText `DELETE FROM [OpenPromoDetails] WHERE [OrderID]=@OrderID; UPDATE [Orders] SET … OUTPUT DELETED.[OrderType], DELETED.[Total] WHERE [OrderID]=@OrderID` ejecutado con `ExecReader(SingleRow)` en modo wrapper (2089-2132; un reader null → NRE en 2133, con el DELETE ya confirmado). Lista SET: `CAID, CustomerID, CustomID, FPID, OtherID, TaxID, BaseAmount, Credit, CustExempt, DeliveryFee, Exchange, ItemTotal, Label, LegalAge, Loyalty, Note, OrderType, OtherAmount, Paid=IIF(@NoTender=1,NULL,[Paid]), Printed=0, TDUsed, TFUsed, TabAlert, Tax, Tip, Total, PhoneNumber, SplitPayments, Surcharge`. **Sin tocar:** `StaffID, InUseID, TerminalID, Status, LegalAgeMethod, LicenseName, Source`. Como `FPID` **sí** figura en la lista SET (lo que zanja la pregunta abierta 1 de reg-other-b), un Save posterior a un Move del plano de salón reescribe `Orders.FPID` con el `_CurrentObj` que el register tenga en ese momento. El bloque ApprovedBy para pedidos existentes (2138-2145) es código muerto (el CommandText se sobrescribe en 2149/2173, y enlaza `@StaffID` y no `@Approver`). Con scheduling licenciado y el total previo a la propina modificado: `UPDATE [Appointments] SET [CheckOutChanged]=1` (2147-2153).

### 3.6 Cambio de tipo de pedido y filas de reparto (2157-2220)

`mChangeType = !mOrderType.Equals(mOldType)` es **true en todo pedido nuevo** (`mOldType` es null). Llama a `OrderManager.RequestRemoteReprint` (2168; con Dapper pone `Orders.RequestRemoteReprint=1` solo cuando algún `OrdersDetails.Printed` vale exactamente 1 — nunca en un pedido nuevo, OrderManager.cs:27-37). Tipo ∈ {Delivery=3, DeliveryService=5, PickUp=2} → `INSERT INTO [OrdersDelivery] (3PDSID, OrderID, DeliveryFee, DeliveryFeeDollar, DeliveryFeeTotal, Status=DeliveryStatus.New=0)` (2184-2195), o UPDATE si ya existe la fila (2187); si el tipo se aparta de esos valores → `DELETE FROM [OrdersDelivery] WHERE [ODELID]` (2200-2203); si Delivery/DeliveryService se mantiene sin cambio (no PickUp) → las tarifas se reescriben en cada guardado (2206-2219).

### 3.7 Banderas de fire (2222-2319)

Bajo `mPrintAllow` (hay filas y no se trata de GoPrintOrder/Split/Percent), `mPrintFire` es true en todo pedido nuevo, en Fire Now y en la impresión To-Go Print. Con `RemoteOption.Firing`, se pone `Fire=true` en memoria sobre las siguientes líneas no impresas y, como el bucle de detalle enlaza `@Fire=mRow["Fire"]` (2341), queda **persistido** como `OrdersDetails.Fire=1`.

### 3.8 Líneas de detalle (2321-2498): inserción, actualización, borrado, stock

`Parameters.Clear()` en 2326 descarta los parámetros de la cabecera; cada bloque posterior vuelve a añadir `@OrderID`. Reglas por cada fila de `_OrderDetails`:

- **`ODID` NULL y no `Deleted`** → `INSERT INTO [OrdersDetails] (…) OUTPUT INSERTED.[ODID]` (2425-2440, wrapper) con todas las columnas del DataRow (2328-2419) más `OrderID=MainID` (2380), `CDate` = el CDate de la fila o `mNow` capturado en 2322 (2385), `UserID=Global.UserID` (2387), `LinePos=i` (2342), `Fire` (2341), y `DepartmentID` solo bajo `License.DepartmentalReporting` (2420-2438). `DelUserID, Printed, QtyDel, RefQty` no están en la lista → NULL; `Fire` se enlaza desde la fila, que `_AddRow` inicializa a DBNull (626-628), así que vale **NULL** salvo que `RemoteOption.Firing` lo haya puesto a 1 (2264) — solo pasa a 0 cuando se imprime un ticket remoto/KDS (csPrint.cs:1921-1924). Las unidades HR/MN/SC añaden `INSERT INTO [OrdersTimeCharge] (ODID, OrderID, StartTime=DATEADD(...,-Qty,GETDATE()))` (2442-2450).
- **`ODID` con valor** → reescritura completa de 34 columnas (2455-2468, raw): `DelUserID, ItemID, InstructID, PromotionID, StaffID, Deleted, Description, DiscType, DiscUnit, Fire, LinePos=i, LoyaltyDisc, LoyaltyUnit, ModAllowDisc, ModAllowPromo, ModHas, Modifier, Price, PriceBase, PriceDisplay, PriceSell, Printed=(mChangeType && Printed<>1) ? 0 : Printed (2465), PromoUnit, PromoUnitDisc, PromoLink, PromoQty, Qty, QtyDel, PriceSinTax, PriceTax, Shared, TaxExempt, ToGo, ScannedSKU` — hayan cambiado o no.
- **Stock** — `if (!mRow["Qty"].Equals(mRow["Qty_Old"]))` (2473): `UPDATE [Items] SET [QtyOnHand]=ISNULL([QtyOnHand],0)+@Qty_Old-@Qty, [QtySold]=ISNULL([QtySold],0)+@Qty-@Qty_Old WHERE [ItemID]=@ItemID AND [StockType]=0` (2479-2485, raw; `Qty_Old` DBNull→0 en las filas nunca guardadas), y después `mRow["Qty_Old"]=Qty` (2486); en restaurantes además `UPDATE [FreshSheet] SET [Qty]=max(0, …)` (2490-2495). Esto zanja la redacción de reg-totals: la venta descuenta stock **en SaveOrder** (primer clic de Save, Send, Print o de una forma de pago), no al completar; los modificadores se incluyen ("TODO Modifier can go negative", 2478).
- **Líneas eliminadas** — cada `csDelRow` encolado en `_Delete` (btnDeleteClick 13545/13569/13582, `_ModifyQty` 9131/9141, 6777, 9560): si `ItemID` es Comment=10 / CommentPrice=8 → `DELETE FROM [OrdersDetails] WHERE [ODID]` físico (2512); las demás heredan opcionalmente el `LinePos` del padre (2521-2526) y luego sufren un borrado lógico `UPDATE [OrdersDetails] SET [CDate]=GETDATE(), [DelUserID]=@UserID, [Deleted]=1, [Printed]=-[Qty], [QtyDel]=ISNULL([QtyDel],0)+[Qty] OUTPUT INSERTED.[Qty]` (2530-2535; `Qty` en sí no cambia) y restituyen stock con `UPDATE [Items] SET [QtyOnHand]=ISNULL([QtyOnHand],0)+@Qty, [QtySold]=[QtySold]-@Qty WHERE [ItemID] AND [StockType]=0` (2540-2545). Nada llega a la base de datos hasta este guardado; `Clear()` vacía `_Delete`, así que "Remove line" y luego "Clear All" dejan las filas con `Deleted=0`.

### 3.9 Pagos (2554-2620)

El bucle usa `_Payments.Select()` — **todas** las filas, ignorando el filtro de la vista. Las filas con `Deleted=true` y `PayID` se borran de forma lógica: `UPDATE [Payments] SET [OrderID]=-[OrderID] WHERE [PayID]` (2564-2569, raw). Las filas con `PayID` NULL se insertan (2595-2607, wrapper `ExecScalar` → `PayID` a la fila):

| Columna | Valor |
|---|---|
| `BankID` | `Global.LocalSetting.BankID ?? NULL` (2598) |
| `CCTxID` | valor de la fila (NULL en este punto; lo escribe después `csPayment.SaveTrx`) |
| `OrderID`, `TerminalID`, `UserID` | `MainID`, `Global.TerminalID`, `Global.UserID` |
| `RefID`, `Reference`, `Amount`, `Value`, `TDUsed`, `TFUsed` | valores de la fila (`PromotionID` del cupón; motivo del pay-out `CustomID`/43; número de cheque; efectivo cobrado …) |
| `RefundAmt`, `Status` | literal `0`, literal `PayStatus.Open=0` |
| `Type` | el `Type` de la fila, con `CardOnFile=20` almacenado como `Credit=3` (2583) |
| `FeeFromTips` | `Tip × TenderFee.Amount` solo cuando hay `DeductFeeFromTips` y el tipo **original** es Credit=3/Debit=4 (2585-2588); las filas CardOnFile reciben NULL |
| NULL | `CloseUserID, CloseDate, Tip, Surcharge, MachName, DeliveryFee, FromSplit`; `CDate` por defecto |

Las filas con `PayID` reciben `UPDATE [Payments] SET [Amount],[TDUsed],[TFUsed],[Type] WHERE [PayID]` (2613-2617, raw) — `Status`, `Value` y `Reference` quedan intactos. `_LoadOrder` carga las de `Status<>9`, así que las filas `Deleted=14` también se reescriben.

### 3.10 Impuestos, promociones, propinas, cliente y auditoría (2622-2774)

- **OrdersTax**: 30 ranuras; la ranura `i<4` siempre, y las demás cuando `Global.Tax.TaxValue[i]!=0 && (qty or exempt ≠ 0)`. `INSERT … (CDate=DateTime.Now, OrderID, Qty=_TaxQty[i], QtyExempt=_TaxQtyEx[i], Type=i) OUTPUT INSERTED.[OTID]` cuando no queda ningún OTID reutilizable (2640-2644); si no, `UPDATE [OrdersTax] SET [Qty],[QtyExempt],[Type] WHERE [OTID]` en FIFO (2649-2654); los OTID sobrantes quedan con `Qty=NULL, QtyExempt=NULL, Type=NULL` (2660-2677) — nunca se borran.
- **OpenPromoDetails**: se reinsertan desde `_OpenPromo` (`ODID, OrderID, Type=DiscType, Value`, 2683-2693); la ruta del pedido existente los borró antes (2090). Al completar se copian a `OrdersPromoDetails` y se borran (18629-18635).
- **OrdersTips** (solo con `Register.Tip.Tips`): `DELETE FROM [OrdersTips] WHERE [TipID]` para las filas `Deleted=1` que tienen TipID (2702-2708); `INSERT (OrderID, StaffID, Amount, Type, Value) OUTPUT INSERTED.[TipID]` para Type ∈ {Group=2, Amount=3, Percent=4} con TipID NULL (2724-2727; `PayID` NULL); en el resto de casos `UPDATE [OrdersTips] SET [Amount],[Type],[Value] WHERE [TipID]` (2733-2737). Las propinas CC=1/CFD=7/Terminal=5 las escribe el flujo de pago, no este.
- **Customers**: `ClubNew`, `ClubNumber` cuando el club ha cambiado (2745-2752). **Log**: una fila por cada entrada de `DataLog` (autorizaciones de manager sobre los botones de Register; `RecID=MainID`, `Module=Transactions`, 2758-2773).

### 3.11 Impresión y reinicio (2776-2815)

`_RowModified` se sobrescribe con un indicador de "imprimir un recibo" (2778). To-Go → `OrderDetailsAsync(PrintType.ToGo)` (2780); si no, indicador de recibo o `RemoteOnSave` → `RemoteAgain|ReceiptAndRemote|Remote|Receipt` (2783-2794); si no, `IncludeTab && _HasTabAuth` → `_PrintOrder` (2805; `UPDATE [Orders] SET [Printed]=1 OUTPUT INSERTED.[FPID],INSERTED.[TabAuthID]` 9933). Las impresiones de tipo remoto ejecutan `UPDATE [OrdersDetails] SET [Printed]=ISNULL([Qty],0), [Fire]=0 WHERE [ODID]` por cada línea (csPrint.cs:1921-1932) de forma **síncrona** (csPrint.cs:3083 `WaitOne`); `PrintType.Receipt=5` (guest check) nunca toca `OrdersDetails` (guarda de csPrint.cs:1892). Por último, `_CmdSave.Enabled=_RowModified=Modified=false` y `return true` (2808-2815). `SaveOrder` devuelve false solo desde la validación; tras el primer INSERT/UPDATE la única salida restante es una excepción.

### 3.12 Lista completa de escrituras de un `SaveOrder`, en orden

| # | archivo:línea | tabla | op | condición | ejecución |
|---|---|---|---|---|---|
| 1 | 2030 | Orders | INSERT (3.4) | `mIsNew` | wrapper |
| 2 | 2055 | OrdersLog | INSERT Create=0, `ID`=campo StaffID | `mIsNew` | raw |
| 3 | 2063 | OrdersLog | INSERT ApprovedBy=5, `ID=_AgeApprover` | `mIsNew && _AgeApprover` con valor | raw |
| 4 | 2084 → csObject.cs:472 | FloorPlan | `OrderID=MainID, InUse=0, Status=3, LastAct=GETDATE()` | pedido nuevo, plano de salón, mesa seleccionada | wrapper |
| 5+6 | 2090, 2093 | OpenPromoDetails; Orders | DELETE; UPDATE (3.5) | pedido existente | un único CommandText, wrapper ExecReader |
| 7 | 2150 | Appointments | `CheckOutChanged=1` | existente, scheduling licenciado, total previo a la propina cambiado | raw |
| 8 | 2168 → OrderManager.cs:37 | Orders | `RequestRemoteReprint=1` | el tipo cambió y hay una línea con `Printed=1` | Dapper |
| 9-12 | 2184 / 2187 / 2200 / 2209 | OrdersDelivery | INSERT / UPDATE / DELETE / UPDATE de la tarifa | 3.6 | raw |
| 13 | 2425 | OrdersDetails | INSERT por cada línea nueva, OUTPUT ODID | `ODID` NULL, no Deleted | wrapper |
| 14 | 2447 | OrdersTimeCharge | INSERT | unidad HR/MN/SC | raw |
| 15 | 2456 | OrdersDetails | UPDATE de 34 columnas por cada línea guardada | `ODID` con valor | raw |
| 16 | 2480 | Items | movimiento de stock | `Qty != Qty_Old`, StockType 0 | raw |
| 17 | 2491 | FreshSheet | qty | dentro de 16, restaurante | wrapper |
| 18 | 2512 | OrdersDetails | DELETE físico | Comment/CommentPrice en cola | raw |
| 19 | 2521 | OrdersDetails | `LinePos`=el del padre | en cola junto al padre (9560) | lote raw |
| 20 | 2530 | OrdersDetails | borrado lógico, OUTPUT Qty | en cola, no comentario | wrapper |
| 21 | 2540 | Items | restitución de stock | tras 20, ItemID con valor | raw |
| 22 | 2564 | Payments | `OrderID=-OrderID` | `Deleted=true` con PayID | raw |
| 23 | 2595 | Payments | INSERT (3.9), OUTPUT PayID | `PayID` NULL | wrapper |
| 24 | 2613 | Payments | `Amount, TDUsed, TFUsed, Type` | `PayID` con valor | raw |
| 25-27 | 2640 / 2649 / 2671 | OrdersTax | INSERT / UPDATE FIFO / puesta a NULL | por ranura | wrapper / raw / raw |
| 28 | 2683 | OpenPromoDetails | INSERT por cada `_OpenPromo` | `_OpenPromo.Count!=0` | raw |
| 29-31 | 2702 / 2724 / 2733 | OrdersTips | DELETE / INSERT / UPDATE | propinas activadas | raw / wrapper / raw |
| 32 | 2745 | Customers | `ClubNew, ClubNumber` | el club cambió | raw |
| 33 | 2758 | Log | INSERT por cada fila de DataLog | autorizaciones de manager | raw |
| 34 | 2780/2783 → csPrint.cs:1922 | OrdersDetails | `Printed=Qty, Fire=0` | impresión de tipo remoto | wrapper, síncrona |
| 35 | csPrint.cs:2899 → OrderManager.cs:243 | Orders | `RequestRemoteReprint=0` | el indicador valía 1 y se imprimió un ticket | Dapper |
| 36 | 2805 → 9933 | Orders | `Printed=1` | `IncludeTab && _HasTabAuth`, sin RemoteOnSave | wrapper |
| C1 | 19407 / 6399 / 6413 / 13973 / 16543 → 739 | Orders | `InUseID=NULL` | el `Clear()` del llamador justo después de un guardado correcto | raw |
| C2 | 721 | Appointments | `OrderID=MainID, Completed=1` | Check Out | raw |
| C3 | 14060 | Orders | `Tax=_Tax, Total=_Total` | recuperación con Join | raw |
| C4 | 18352 | Payments | INSERT `Status=1, Type=Cash=0, CloseDate=GETDATE()` | inserción propia de btnProcessClick para las filas de cash-back con PayID NULL (Type -1/1/13) | wrapper |

### 3.13 Save/hold frente a Pay frente a Complete — cómo queda la base de datos tras cada uno

| Sender | Orders tras el clic | Payments | Bloqueo |
|---|---|---|---|
| Save (`_CmdSave`) | `Status=0`, `Paid=NULL` (sin forma de pago) o sin cambios, `Printed=0`, totales según lo calculado; OrdersLog Create (pedido nuevo) | ninguno nuevo; las filas Discount/parciales preparadas se insertan con `Status=0` | `InUseID=NULL` (Clear) |
| Pay (botón de forma de pago) | igual que Save, luego por cada forma de pago `Paid+=Amount, Status=6`, y después el cierre de lote `Status=1, CloseDate, BatchID=0, Tip=_TipAmt` (en segundo plano) | se insertan con `Status=0`, luego batch A `Status=1/6/8, CloseDate` | `InUseID=TerminalID` hasta el `Clear()` del hilo de interfaz posterior a completar |
| Tab Auth (`_TndTabAuth1`) | igual que Save; luego `TabAuthID=PayID, TerminalID, Label, TabMin, TabLimit`; `Status` se queda en 0 | `Status=11, Value=AuthAmount, CCTxID` | `InUseID=NULL` (17537 `_DefaultScreen`) |
| Tab Complete (`_TndTabComplete1`) | SaveOrder solo si hay `Modified` (si no, escribe el cálculo previo 16572/16589/16619); captura; `TabAuthID=NULL`; luego igual que Pay | la fila del tab pasa a `Status=1` | igual que Pay |
| Ventana Complete (`Complete(object)` 1061 → frmComplete → `_OnComplete`) | sin `SaveOrder`; escribe `csPayment.Complete` (sección 8) | `Auth=6 → 1` | sin tocar |

## 4. Preparación de formas de pago y cálculo de totales

### 4.1 Las tablas en memoria

`_OrderDetails` y `_Payments` son DataTables de solo esquema construidas por `Lib.TableFromDB` (`SELECT *, … FROM [Payments]` con `CommandBehavior.SchemaOnly`, 1632-1640; Lib.cs:7231-7244): cada columna física existe en cada DataRow, más columnas auxiliares que nunca se persisten (`_Payments`: `Deleted, Manual, PayStatus, PayType`; `_OrderDetails`: `Allow_Disc, Credit, Exchange, Ext, Promo_*, Qty_Old, Qty_Total, To_Process`, …, 1591-1612). `_Payments.DefaultView.RowFilter = [Deleted]=0 AND ([Status]=0 OR [Status]=1 OR [Status]=6)` (1638) alimenta la cuadrícula y `_PayUpdate`; `SaveOrder` ignora la vista (3.9). Los pedidos recuperados llenan `_Payments` con `SELECT *, dbo.PayType(Type), dbo.PayStatus(Status), 0 AS Deleted FROM [Payments] WHERE OrderID=@OrderID AND Status<>9` (8794-8800).

**Pulsar un botón de forma de pago no escribe nada en las tablas del pedido.** `btnPayClick` (15551-15918) resuelve el importe — `frmCash` para Cash (`Amount<=balance && Collected>=Amount`, frmCash.cs:101-103; Exact Cash fija `mValue=mAmount=mBalance` 15653-15654), `frmPayOut`/`frmPayOutAmount` para los pay-out (`RefID=CustomID or 43`, 15686-15723), una INVOICE de CRM para On Account (`AccountReceivableManager.CreateTransaction`, 15733 — una escritura **remota** ya en el momento de la preparación), `frmMessageValue` para el resto — y a continuación `_AddPayment(type, balance, amount)` crea la fila `Amount, Deleted=false, PayType=<name>, Status=PayStatus.Open=0, Type` (4595-4602) y `_UpdatePay` calcula `TDUsed`/`TFUsed` (12583-12690: `TDUsed=Round(amount×TenderDisc.Amount[type],2)`; `TFUsed` sale de `_CalcTFUsed` limitado a `_TFUsed`, usando `_Payments.Compute("SUM([TFUsed])")`, que incluye las filas con `Deleted=true`, 12645). La fila pasa entonces a llevar `RefID, Reference, Value, Manual` (15896-15899) y `btnProcessClick(sender, EventArgs.Empty)` se ejecuta de forma **síncrona** dentro del mismo clic (15904). Hay manejadores especializados que preparan `Discount=5` (15961-15969, `TDUsed=TFUsed=DBNull`), `EBTCash=18`/`EBTFood=6` (16017-16023 / 16068-16074, sin consulta de saldo al procesador), `Reward=10`/`Factor4Loyalty=17` (16234). Los botones Gift Card / Gift Certificate son **ventas de artículo** (línea de `_OrderDetails` con `ItemID=6`/`5`, `Qty=1`, todas las columnas de precio = importe, 14117-14146 / 14185-14210), no formas de pago.

`_ResetPay` (19346-19387) elimina las filas Open cuyo `PayID` es DBNull y marca las ya persistidas con `Deleted=true` (→ `OrderID=-OrderID` en el siguiente guardado). `_RemovePayment` (4638-4651) elimina físicamente una fila sin marcarla — lo usa `_CancelGiftCertificatePayment` (4653-4661), cuyo `Select("[PayType]='Certificate'")` también atrapa filas de certificado Completed de un pedido recuperado (pregunta abierta).

Escrituras colaterales alcanzables desde la preparación y el registro de artículos (ring-up), ninguna de las cuales toca tablas del pedido: `Customers.ClubNumber` en el diálogo "Missing membership number" (7784-7788); INSERT/UPDATE de `Customers` desde el diálogo de teléfono de ScanData a través de frmDelivery (Transactions\Delivery\frmDelivery.cs:342/350, auditado mediante `AuditManager.WriteLog`); `Items.Active=1` para un artículo inactivo escaneado (5185-5189); recategorización JUUL de `Categories/SubCat/Items` (5552-5626); Auto-Move-Bulk de `Items/ItemsTx/ItemsTxDetails` (6702-6743, raw, lanza); filas de `Log` para los diálogos de PIN de manager (solo en Release). La ruta de A/R "Take Payment" escribe `AccountPayments`/`AccountPaymentDetails` dentro de una transacción real (AccountPaymentManager.cs:34-69) después de ejecutarse el procesador. Otras escrituras colaterales del ring-up (artículo "86", filas de cliente del recibo por correo electrónico, `Customers.Distance`, filas de dirección de reparto) se listan en 9.11.

### 4.2 Fórmulas (`_TotalUpdate` 11409-12575, `_PayUpdate` 9654-9919)

| Magnitud | Fórmula | Dónde |
|---|---|---|
| `OrdersDetails.Price` | `Lib.RemoveTax(row)` — precio de lista sin el impuesto incluido; si no, `PriceBase` | 5820, Lib.cs:6309-6343 |
| `OrdersDetails.PriceSell` | `Price − DiscUnit − PromoUnit − LoyaltyUnit`, luego los cupones y por último se reescribe como `(PriceSell×Qty − Discount-tender share)/Qty` — neto de todo | 11602-11653, 11737-11767, 11865 |
| `Orders.BaseAmount` | `Round(Σ Price×Qty, 2, AwayFromZero)` | 11810, 12529 |
| `Orders.ItemTotal` | `Σ PriceSell×Qty` (**4 decimales**, sin redondear; no lo redondea ni el lote de cierre ni 16794 — resuelve la pregunta abierta 4 de reg-totals; la reescritura de precio dual sí, csDualPriceManager.cs:690) | 11851-11854 |
| `_SubTotal` | `Round(ItemTotal − TDUsed, 2)` | 12531 |
| `OrdersTax.Qty[i]` | base imponible por ranura; las porciones pagadas con EBT se trasladan a `QtyExempt` | 11970-12346 |
| `Orders.Tax` | se reconstruye a partir de los tramos redondeados: `Σ Round(Round(_TaxQty[i],2)×TaxValue[i],2)` + el impuesto de la comisión por forma de pago (los pedidos íntegramente TaxIncluded usan un truco de truncado/redondeo) | 12459-12511 |
| `OrdersDetails.PriceTax` / `PriceSinTax` | `mTax/Qty` / `TaxType×TaxRatio×…` (0 si está exento) — `SUM(PriceTax×Qty)` puede diferir de `Orders.Tax` en un centavo | 12404-12408, 12365-12390 |
| `Orders.OtherAmount` (`_OtherAmt`) | cupón Other + `_TFUsed` + `_Surcharge` + `_DeliveryFee` | 12516-12527 |
| `Orders.Total` | `_SubTotal + _Tax + Round(_TipAmt,2) + _OtherAmt`; para un pedido con precio dual ya guardado y pagado con una forma de pago DP, `DualPriceOrderModel.Total + tip + other` (el cargador traga las excepciones y devuelve 0) | 12540, 12533-12538 |
| saldo | `_Total − _TotalPay`; `_TotalPay` suma las filas de la vista **excepto** Discount=5, Coupon=2, ClubDiscount=15 (que nunca cuentan como pago); `_Paid` suma solo las filas Completed/Auth/Refunded/Voided | 1210, 9705-9777 |
| `Orders.TDUsed/TFUsed/Surcharge` | Σ de los `TDUsed/TFUsed/Surcharge` de cada pago sobre la vista | 9709-9722 |
| tope del importe EBT | las formas de pago EBT se limitan a los importes de los artículos elegibles para EBT ("Maximum EBT amount applied") | 1221-1291 |
| fila de pago del cupón | la fila propia de `_Payments` de un cupón Percent (`Type=2, Status=0, RefID=PromotionID`, creada en 411-418) recibe `Amount=Round(mSubPromo,2)` en cada `_TotalUpdate` — eso es lo que guarda `Payments.Amount` | 11770 |

`_TotalUpdate` se vuelve a ejecutar ante cada cambio (unos 73 puntos de llamada); todo lo que está en memoria se recalcula, así que conviene aplicar aserciones solo sobre lo que `SaveOrder` escribió. Un `_TotalUpdate` que lanza deja las DataTables a medio actualizar y la base de datos intacta.

## 5. Pago y finalización: `btnProcessClick` (16277-19115)

### 5.1 Punto de entrada y precondiciones

Toda ruta de forma de pago termina en `btnProcessClick`: `btnPayClick` (15904), Exact Cash a través de `btnPayFullClick` (16089-16102; en 16430-16435 se prepara además una **segunda** fila Cash para `_GetBalance(Cash)`, normalmente 0 — pregunta abierta), los botones de denominación `btnCash1..6` (fila preparada en 16437-16443), los botones Tab Auth / Tab Complete (4255/4266), Discount completo (15983), EBT (16035/16086), Reward (16235). `sender==btnClear` no es un clic: `btnPayClick` lo sustituye cuando un diálogo se respondió "Yes" (15677/15780) y su único efecto es `mCanPrint=false` (16687).

Comprobaciones (sin cambios en la base de datos salvo que se indique lo contrario): diálogo de empleado (16290); la comprobación "already paid online" de ScanToPay está muerta (Int16 boxeado frente a Int32, 16305-16327); cliente obligatorio para Delivery/DeliveryService sin 3PDS ni `ReqCustomer` (16332-16352; `btnFindCustomerClick` puede lanzar `UPDATE [Customers] SET [ClubNumber]`, 7784-7788); un tab creado en otro terminal → `SaveOrder`, `Clear()` (`InUseID=NULL`) y salida (16367-16390, 16528-16547); `mRows` = filas no borradas con `Status=TabAuth=11`, o con `Amount<>0` y `Status` ∈ {Open=0, RemoteError=5} (16445-16451); un pedido de cortesía (`_Total==0 && _BaseAmount>0`) añade automáticamente una fila `Discount=5` y fija en cada línea `PriceSell=Price` (16460-16476); los cupones por encima de `_BaseAmount` abortan (16486-16497); con una mesa del plano de salón, las filas Credit se procesan al final (16503). Después, **`if (Modified) SaveOrder(sender)`** (16635-16637): Orders/OrdersDetails/OrdersTax y una fila `Payments` por cada forma de pago preparada, con `Status=0, RefundAmt=0`, están en disco **antes** de cualquier llamada al procesador; el stock ya está decrementado. On Account enlaza su transacción de CRM con `MainID` o la elimina y salta a `WithError` (16644-16665).

### 5.2 Cálculo previo de Tab Complete (16549-16627, solo cuando `!Modified`)

Con descuento/comisión por forma de pago habilitado para Credit: `_TFUsed==0` → `_UpdatePay(Credit, tab row, _OriginalBal)` y, bajo precio dual, `UPDATE [Payments] SET [Amount]=DualPriceOrderModel.Total WHERE [PayID]=_TabAuthID` (16572); después, cuando `!IsDualPrice`, `UPDATE [Orders] SET [Tax],[TDUsed],[TFUsed],[Total]; UPDATE [Payments] SET [TDUsed],[TFUsed] WHERE [PayID]=_TabAuthID` (16586-16589, lote). Sin TD/TF: `UPDATE [Payments] SET [Amount]=_Balance WHERE [PayID]=_TabAuthID` (16619). Cuando el pedido **sí** está modificado no se ejecuta nada de esto: en su lugar `SaveOrder` reescribe la fila del tab (1927-1954, 2613).

### 5.3 Propina y firma en CFD, antes de leer la tarjeta (16741-16813)

En cajas multitouch (`Global.TouchScreen` y tipo de pedido Regular/PickUp/TakeOut, 16691), con propinas o firma CFD habilitadas y una forma de pago de tipo tarjeta, se ejecuta primero `Global.CFD.TipAndSignature`. Una propina distinta de cero ejecuta un solo lote: `UPDATE [Orders] SET [Total]=[Total]+@Amount; INSERT INTO [OrdersTips] (OrderID, PayID=row PayID, StaffID, Amount, Type=CFD=7, Value) OUTPUT INSERTED.[TipID]` (16794-16798, wrapper; el TipID se guarda en `mValues.Object4`). En la base de datos, `Orders.Total` siempre suma la propina; en memoria solo cuando `!_HasSplitPayments` (16768-16770). Cancelar → `WithError`.

### 5.4 Por tipo de forma de pago

| Forma de pago | Qué ocurre antes del commit | Escrituras en la base de datos propias del tipo |
|---|---|---|
| **Cash** (0) / DeliveryService (16) (16820-16879) | diálogo `frmCash` cuando `Value` es NULL (cancelar → `WithError`; la fila Cash se queda en `Status=0` porque WithError solo borra los tipos de tarjeta); el cajón de dinero se abre una vez por clic para la primera fila Cash (16863-16867, sin efecto en DEBUG) | precio dual después de una forma de pago con tarjeta: `Payments.TFUsed` (csDualPriceManager.cs:278 via 16858) |
| **Credit/Debit/EBTCash/EBTFood/CardOnFile — pago nuevo** (17108-17336) | `CardData{OrderID=MainID, PayID=row PayID, AccountType via Lib.GetAccountTypeFromPayType (Credit=2, Debit=0, EBTCash=3, EBTFood=5, CardOnFile=2, Offline=6), Amount, TxType=Return=5 (exchange) / Auth=0 (restaurant Credit, mIsAuth 16894) / Sale=2}` (17120-17131); `Global.Processors.Process` (17330) gobierna el dispositivo (simulado en esta compilación) | con aprobación, `SaveTrx`: `INSERT INTO [CCTxs] (…) OUTPUT INSERTED.[CCTxID]` con `Status = Auth ? Authorized=0 : Completed=1`, `TxType`, `AType`, `AuthCode`, `PNRef`, `TransmitID`, `CCNumber='****'+last4`, `Partial=Amount>AuthAmount`, `Source` (MANUAL=0 si es manual), `Tip=IIF(0,NULL)`, `Surcharge` en NULL cuando vale 0 (csPayment.cs:93-104) y luego `UPDATE [Payments] SET [CCTxID],[Reference]=@AuthCode WHERE [PayID]` (112-118) — batch A sobrescribe más tarde `Reference` con el valor de la fila preparada (NULL para tarjetas), así que solo `CCTxs.AuthCode` conserva el código; rechazo → `INSERT [CCTxFailures]` (132-140, 676-697); el recargo del procesador se suma al Amount de la fila (17346-17351); precio dual `Payments.TFUsed` + `Orders.Total` (csDualPriceManager.cs:278/301 via 17379-17380); la primera aprobación Credit online sella `UPDATE [Terminals] SET [AuthDate]=GETDATE(), [Sent]=NULL WHERE [AuthDate] IS NULL` (17454) |
| — tarjeta manual (17264) / Card on File (17283) | `frmReadCardManual` (`Source=MANUAL=0`); Card on File vincula el token del CRM y se reescribe como Credit con `ManualCard=true` (17285-17301) | igual que arriba; SaveOrder ya había guardado `Payments.Type` como 3 |
| — offline (17145-17259) | se rechazan CardOnFile/Debit/EBT; entrada manual; límites por pedido/total/días; `Global.MainForm.Offline(true)` persiste el modo offline en `Exatouch.cfg` (17211); `AuthCode` falso (caracteres de un GUID con `O` en el índice 2 y `L` en el 4, 17228), `CardData.Reference='Offline'` (nunca se copia a la fila: `Payments.Reference` acaba en NULL), respuesta `OFFLINE` | `SaveTrx` (17235, se ignora el resultado; `CCTxs.Status=0` cuando es Auth de restaurante, `TxRespons='OFFLINE'`/NULL) y después el lote `UPDATE [CCTxs] SET [Number]=RSA(PAN+FS+Address+FS+Zip); UPDATE [Payments] SET [Type]=Offline=11; UPDATE [Terminals] SET [OffDate]=GETDATE(),[OffSent]=NULL WHERE [OffDate] IS NULL` (17237-17255). Una venta con tarjeta offline en restaurante termina como `CCTxs.Status=0` / `Payments.Status=6` |
| — aprobación parcial (17354-17443) | activa en PAX/Dejavoo (`Default.SemiIntegrated` se salta la anulación automática de csPayment, csPayment.cs:832-867): `frmPartialApproval` | Cancel: `UPDATE [Payments] SET [Amount]=AuthAmount` (17395), `VoidAuth(PayID,true,true)` (17403: CCTxs `Status=3, Tip=0, TxType=6`; Payments `Status=9, CloseDate`; Orders/OrdersTips quedan intactas porque `pVoidOrder=true`; `Terminals.OffDate/OffSent=NULL`), recálculo opcional de TD/TF + `SaveOrder` (17416-17423) y luego `WithError` (la fila no sufre borrado lógico porque `mPartial`). Accept: `UPDATE [CCTxs] SET [CloseDate]=GETDATE(),[Status]=1,[Tip]=0,[TxType]=Capture=1` (17432), fila `Amount=AuthAmount, Value=Amount`, `mIsAuth=false` |
| — **inicio de Tab Auth** (17464-17540, restaurante) | la etiqueta sale del titular de la tarjeta o de `frmLabel`; se autoriza `Value=_TabMin` | lote `UPDATE [CCTxs] SET [Status]=TabAuth=2; UPDATE [Orders] SET [TabAuthID]=@PayID,[TerminalID]=Global.TerminalID,[Label]=ISNULL([Label],@Label),[TabLimit]=(partial ? AuthAmount : NULL),[TabMin]=_TabMin; UPDATE [Payments] SET [Status]=TabAuth=11,[Value]=AuthAmount` (17486-17514); FloorPlan `Status=IIF(Status=10,10,7), LastAct=GETDATE()` (17521) o `DBUpdate` (17532); recibo; `_DefaultScreen()` → `InUseID=NULL` (17537); **return** — `Orders.Status` sigue en 0, `Paid` sin cambios, `Payments.CloseDate` en NULL |
| — **Tab Complete** (16904-17107) | `Global.Processors.Complete(_TabAuthID, tip)` (16915; `tip=null` sin multitouch, `_TipAmt` con multitouch) hace la captura: devuelve -1 si ya estaba capturada (csPayment.cs:330-337), si la propina se cancela o supera el tope (353-374), si hay desconexión (396-401) o si falla el dispositivo (458-464, fila `CcTxFailures`) | éxito: `CCTxs SET CloseDate=GETDATE(), PNRef, Status=1, TransmitID, Tip, TxType=Capture=1` (csPayment.cs:483-491); con `tip==null` también `Payments SET CloseUserID=Orders.StaffID, Amount+=Tip, Status=1; Orders SET Paid+=Tip, Tip+=Tip, Total+=Tip` (495-509); filas de recargo (517-532); INSERT/UPDATE en `OrdersTips` con `Type=CC=1` (o CFD=7 con TouchScreen) (547-561); con TouchScreen, `Payments.Amount+=Tip` **otra vez** (565-569); `FeeFromTips` (575-579). Después, con comisión por forma de pago activa → `Payments.TFUsed` (17014) + `Orders.TFUsed` (17024); si no, con recargo activo → lote `Payments.Amount=row Amount+Surcharge; CCTxs.Surcharge; Orders.Surcharge=SUM` (17061); siempre `UPDATE [Orders] SET [TabAuthID]=NULL` (17085); tab offline → `Payments.Type=Credit=3` (17097); `mIsAuth` se limpia (16995). Fallo (-1): TD/TF se ponen a cero en memoria, `UPDATE [Orders] SET [Tax],[TDUsed],[TFUsed],[Total]; UPDATE [Payments] SET [TDUsed]=0,[TFUsed]=0` (16956) cuando aplican TD/TF, y **return** — la fila del tab se queda en 11 y `TabAuthID` intacto |
| — propina en el terminal (17546-17594) | sin multitouch, `Account.TipReq`, propina en el PIN-pad | lote `UPDATE [Orders] SET [Total]=[Total]+@Tip; UPDATE [CCTxs] SET [Tip]=NULL; INSERT [OrdersTips] (…, Type=Terminal=5) OUTPUT TipID` (17554-17562); fila `Amount+=tip`, `mIsAuth=false` |
| **On Account** (19) (17605-17620) | la INVOICE del CRM ya se creó durante la preparación | solo con precio dual: `Payments.TFUsed` (17615) |
| **eGift** (7) (17622-17748) | importe negativo → cambio encolado; `frmGiftCard` (cancelar → WithError), `GetBalance` opcional (17664), `GiftCardManager.Redeem` (17678, HTTP; el simulador de Training aprueba); "101" del host = parcial (`Amount=mGift.Redeem`, `mPartial=true`); `mIsAuth = IsRestaurant && balance<>0`, `Value`=saldo restante (17693-17694) | `SaveTrx` (17726: CCTxs `CardHolder='eGift'`, `AType=GiftCard=4`, `AuthCode`=referencia del host, `TxType` Auth/Sale; Payments `CCTxID, Reference`); se decrementa la fila local de `GiftCards` si el número coincide con un `GiftCode` local (`Balance-=Redeem, Used=(Balance<=0), UsedDate`, GiftCardManager.cs:105-118 via 17732); `CCTxs.Number`=RSA(card) mediante un UPDATE de fila completa con Dapper (CardTransactionManager.cs:28 via 17735); precio dual `TFUsed` (17744). Batch A sobrescribe después `Payments.Reference` con el valor de la fila (NULL) — la referencia del host solo sobrevive en `CCTxs.AuthCode` |
| **Certificate** (12) (17753-17840) | el código sale de `Reference` o del `frmLabel` "Gift Cert #:"; no encontrado / saldo cero → `_CancelGiftCertificatePayment` → `_RemovePayment` → `RowNotInTableException` en 12598, **abortando el manejador antes de WithError** (la fila del certificado se queda en `Status=0` y aparece la ventana de error global); saldo inferior al importe → `Amount=balance, Value=0, mPartial=true` (17813-17818); si no, `Value=balance−amount` (17826) | `GiftCardManager.ApplyPayment`: `GiftCards SET Balance-=amount, Used=(Balance<=0), UsedDate=now` (GiftCardManager.cs:105-118 via 17829, Dapper); precio dual `TFUsed` (17836) |
| **Reward** (10) / Factor4Loyalty (17) (17845-17924) | solo dentro de `if (_RewardEnabled)` — en caso contrario se confirma como un pago Completed corriente y sin descuento alguno; Factor4 → API de csLoyaltyCard (17857/17869); EPI: `mUsed=ceil(abs(amount))×DollarPerPoint` (17878-17880), rechazado cuando supera `Lib.GetPoint` | EPI: por cada fila candidata (sin ORDER BY, 17891) `UPDATE [Loyalty] SET [Used]=IIF([Used]+@Used<[Points],[Used]+@Used,[Points]) OUTPUT INSERTED.[Used]-DELETED.[Used] WHERE [LoyaltyID]` hasta consumirlo (17907-17919); `Value=mUsed` |
| **Check** (1) / **PayOutCust** (13) / **CheckCashBack** (14) | las filas Check se omiten en el bucle cuando existe un importe de cash-back (16717); las filas PayOutCust solo reciben `PaymentManager.UpdateTip` (`UPDATE [Payments] SET [Tip]=@Tip`, PaymentManager.cs:25-31 via 16730) | se terminan en la sección de cash-back (5.6) |
| **Coupon** (2) / **Discount** (5) / **ClubDiscount** (15) | las filas ya existen en `Payments` con `Status=0` desde SaveOrder | batch A actualiza la fila; la mitad del lote correspondiente a `Orders` se **omite** (17955) — nunca suman a `Orders.Paid` |

### 5.5 El commit por forma de pago (batch A, 17928-18036)

Tras el `switch`, por cada forma de pago que llega al final del cuerpo del bucle se lanza un CommandText mediante `Lib.ExecNonQuery` (18036, **traga** SqlException):

```sql
UPDATE [Payments] SET [BankID]=@BankID, [TerminalID]=@TerminalID, [UserID]=@UserID
    /* , [Amount]=@Amount, [Tip]=@Tip  -- se omite si el Status en memoria de la fila es TabAuth=11 (17942) */,
    [CloseDate]=GETDATE(), [DeliveryFee]=@DeliveryFee, [Reference]=@Reference, [Status]=@Status, [Value]=@Value, [Surcharge]=@Surcharge
WHERE [PayID]=@PayID;
UPDATE [Orders] SET [Paid]=ISNULL([Paid],0)+@OrderPaidAmount /* se omite en los intercambios */,
    /* [Total]=ISNULL([Total],0)+@Surcharge, [Surcharge]=(SELECT SUM([Surcharge]) FROM [Payments] WHERE [OrderID]=@OrderID), solo si el Surcharge de este pago es <>0 */
    [Status]=@OrderStatus  /* OrderStatus.Pending=6 */
WHERE [OrderID]=@OrderID;   -- la segunda sentencia entera se omite con ClubDiscount/Coupon/Discount (17955)
UPDATE [Payments] SET [FeeFromTips]=… WHERE [PayID]=@PayID;   -- Credit/Debit con DeductFeeFromTips y parte de propina (18008)
```

`@Status` = `Refunded=8` en los intercambios (`Amount<0`), `Auth=6` cuando `mIsAuth` (Credit de restaurante todavía sin capturar; eGift de restaurante con saldo restante) y, en el resto de casos, `Completed=1` (17967). `@BankID=Global.LocalSetting.BankID ?? NULL`, `@TerminalID=Global.TerminalID`, `@UserID=Global.UserID`, `@Value=row Value`, y `@Surcharge` NULL cuando vale 0 (17971-17982). **`@Tip`** = la parte de `(_TipAmt−_TipPaid)` que corresponde a esta forma de pago, prorrateada por `mAmount/(_TotalPay−mPay)` (o el resto cuando cubre lo que queda); solo se escribe cuando `mAmount >= share`, y si no NULL; al escribirse, `mAmount -= share` (17994-18017). `@DeliveryFee` = la parte pendiente del repartidor cuando es `>0` y `<= mAmount`, y si no 0 (18024-18033). Después viene la ramificación CFD/recibo (18040-18272): los pedidos táctiles llaman a `Global.CFD.Complete` (la valoración puede escribir `Orders.Rate`, frmCFD.cs:632); en modo no táctil, Cash/Check/DeliveryService con precio dual escriben `TFUsed` y `Orders.Total` (18124-18125). `mPartial` rompe el bucle (18274): las filas posteriores no se procesan.

Secuencia de `Orders.Status` dentro de un mismo clic en Pay: `0` (INSERT) → `6` (por forma de pago) → `1` (lote de cierre, hilo en segundo plano). Un pedido pagado parcialmente se queda en `6` y frmGetOrders lo lista (`Status IN (0,6)`).

### 5.6 Cash-back de Check / pago en efectivo al cliente (18283-18445)

`mValue = _Pay[CheckCashBack] + _Pay[PayOutCust]` descontando los ya completados. Si no es cero, `mDue=_Total−_Paid`; cuando `mDue<0` se añade una fila sintética `Type=-1, Amount=mDue, Status=Open` (18309-18316). Las filas `[Status]=0 AND [Deleted]=0 AND [Type] IN (1,13,-1)` se procesan en orden de inserción: `Amount>=mDue` → `@Amount=mDue`, y Check→CheckCashBack=14 cuando `Amount>mDue`; en caso contrario `@Amount=row.Amount`. Filas sin PayID: `INSERT INTO [Payments] (BankID, OrderID, TerminalID, UserID, Amount, CloseDate=GETDATE(), RefundAmt=0, Status=1, TDUsed=0, TFUsed=0, Type=Cash=0, Value=0) OUTPUT PayID` (18352, `Orders.Paid` no se ajusta); las demás: lote `UPDATE [Orders] SET [Paid]+=@Amount; UPDATE [Payments] SET BankID, TerminalID, UserID, Amount=@Amount (aplicado), Value=row.Amount (entregado), CloseDate=GETDATE(), Reference, Status=1, Type WHERE PayID` (18361-18373, raw). Cuando `mValue >= mTotal−mPaid`: `INSERT INTO [BankMod] (BankID, UserID, MngrID=Global.MgrID, Cash=-mChangeDue, Date=GETDATE(), Notes='Check '/'Pay Out customer '+' from Order: '+MainID)` si hay una sesión de cajón (bank) con swap-in (18382), cajón de dinero y diálogos de cambio a devolver.

### 5.7 Reconciliación de precio dual (18448-18535)

Cuando se ha usado una forma de pago DP y el saldo del modelo es 0 (±0.02): los totales del formulario se sustituyen por los de `DualPriceOrderModel` y se reescriben — `Orders` (`BaseAmount, ItemTotal, Tax, [DeliveryFee], Total`, csDualPriceManager.cs:690), las columnas de precio/promoción de `OrdersDetails` por ODID (721), `OrdersTax` (773), `OpenPromoDetails.Value` (823), `OrdersDelivery.DeliveryFee` (878), el `Payments.Amount` del cupón (923) y el `Payments.Amount/TFUsed` del descuento de club (995; cuando la comisión es <=0 nunca se enlaza `@TFUsed` → error de SQL, registrado en el log). Las escrituras de 721-995 registran el error y continúan, así que pueden quedar desfasadas respecto a la fila `Orders`.

### 5.8 Finalización (18562-18972)

`_Balance` se pone a cero dentro de un margen de 0.02 cuando hay comisiones (18562). Si `!mPartial && Round(_Balance,2)==0`:

1. Si `_HasTabAuth` y `_Payments.Rows[0]` sigue siendo TabAuth (el tab se cerró con otra forma de pago): `Global.Processors.VoidAuth(_TabAuthID,false,true)` (18572): anulación en el dispositivo (Dejavoo captura primero) y después el lote `CCTxs SET Status=3, Tip=0, TxType=6; Payments SET Amount=IIF(Status=11,Value,Amount), CloseDate=GETDATE(), Status=9; Orders SET TabAuthID=NULL` (csPayment.cs:1105-1148) y `Terminals SET OffDate=NULL, OffSent=NULL` (1173-1181, siempre en las anulaciones online); si falla solo avisa.
2. Se capturan variables locales (18581-18617): `mIsAuth`/`mPayType` guardan los valores de **la última forma de pago procesada**; `mUserID=Global.UserID`; `mGifts` = las filas de detalle con ItemID 5/6 más las filas de intercambio.
3. Un **hilo en segundo plano** (18620) ejecuta el batch C mediante `Lib.ExecScalar` (18671, lo traga): `DELETE FROM [OrdersTimeCharge] WHERE [OrderID]; INSERT INTO [OrdersLog] (OrderID, ID=@StaffID=Global.UserID, Type=Close=1); INSERT INTO [OrdersPromoDetails] (ODID,Type,Value) SELECT … FROM [OpenPromoDetails] WHERE [OrderID]; DELETE FROM [OpenPromoDetails] WHERE [OrderID]; UPDATE [Orders] SET [BatchID]=@BatchID (literal 0, NULL cuando existe un artículo con código de impuesto — no la sesión de cajón), [CloseDate]=GETDATE(), [Status]=Completed=1, [Tip]=@Tip (=_TipAmt, la propina completa del pedido), [Surcharge]=@Surcharge (SUM(Payments.Surcharge) vía Dapper cuando TenderFee.Surcharge, NULL cuando vale 0) OUTPUT INSERTED.[FPID]` (18622-18671). Esto se ejecuta **también cuando los pagos siguen en Auth=6** (restaurante): el pedido queda Completed mientras su pago con tarjeta espera la captura.
4. Cliente (mismo hilo): EPI `INSERT INTO [Loyalty] (CustomerID, OrderID, Points=int(mLoyalty)×PointPerDollar, Used=0)` (18713, solo con `_RewardEnabled`, `_Loyalty<>0`, `Customers.Loyalty=1`, `Loyalty.Selected` y sin Factor4) o la API de Factor4 (18700); venta de club `UPDATE [Customers] SET [Club]=_ClubNew, [ClubNew]=NULL, [ClubEnd]=…, [ClubStart]=ISNULL([ClubStart],today)` (18729); estadísticas de visitas `UPDATE [Customers] SET [FirstVisit]=ISNULL(...), [HowManyVisits]+=1, [PrevOrder]=[LastOrder], [LastOrder]=@OrderID, [RecentVisit]=GETDATE()` (18741).
5. Artículos de regalo: cada línea `ItemID=GiftCard=6` → `GiftCardManager.Activate` (18777, HTTP); `IssueCertificate` la convierte en `Certificate=5`; `CanceledByUser` dispara `OrderManager.VoidOrder` mediante `BeginInvoke` (18793 — el **único** llamador de la anulación transaccional, sección 6.1). Filas de certificado: `INSERT INTO [GiftCards] (CustomerID=Lib.ToInt(_CustomerID) (0 para clientes sin ficha), OrderID, UserID, Amt=Balance=Price, DateSold=now, FromRefund=0, Used=0, GiftCode) ; SELECT SCOPE_IDENTITY()` (GiftCardManager.cs:370-402 via 18816; con importe<=0 lanza `ArgumentOutOfRangeException` en el hilo) y luego `UPDATE [OrdersDetails] SET [ItemID]=5, [Description], [SKU]=GiftCode WHERE [ODID]` (OrderManager.cs:232 via 18824).
6. Plano de salón mediante `BeginInvoke` (18839-18925): mesa dividida (Split) + `mIsAuth` → `UPDATE [OrdersFPSplit] SET [Status]=Authorized=5 WHERE [OrderID]` (18868); mesa dividida en cualquier otro caso → `DELETE FROM [OrdersFPSplit] WHERE [OrderID] AND [FPID]; SELECT TOP 2 remaining` (18877) y, cuando queda exactamente una, `DELETE … WHERE [FPSID]` (18895); después `csObject.DBUpdate` → `FloorPlan SET OrderID, InUse=0, Status, LastAct=GETDATE()` — sin filas de split → `OrderID=NULL, Status=CleanUp=6`; con 1 restante → ese pedido y su estado; sin división y con `mIsAuth` → `Status=Authorized=5`; en el resto `OrderID=NULL, Status=6` (csObject.cs:472 via 18890/18902/18923). **Si no hay ningún control csObject de la mesa en `pnlObjects`, el `new csObject()` de reserva tiene `ID=DBNull` y `DBUpdate` no toca ninguna fila** (18849).
7. Impresiones (ticket remoto cuando `GoPrintOrder`, certificado de regalo, recibo — `csPrint.cs:500` puede hacer `UPDATE [Payments] SET [TerminalID]` si es NULL) y Change Due (18930-18941).
8. Mientras tanto, el **hilo de interfaz** pone `Modified=false` y ejecuta `Clear()` (directamente en una mesa dividida (Split) y después `_CurrentObj.DoClick()`; si no, mediante `_DefaultScreen(1)`) → `UPDATE [Orders] SET [InUseID]=NULL` (739), con `_Lock()` opcional (18950-18971). Si no se completa → solo `_TotalUpdate/_PayUpdate`; `Orders.Status` se queda en 6.

### 5.9 `WithError` (18982-19113)

Las formas de pago no parciales y no TabAuth de Type Credit=3/Debit=4/EBTCash=18/EBTFood=6/eGift=7/Offline=11/OnAccount=19/Certificate=12 sufren un borrado lógico: `UPDATE [Payments] SET [OrderID]=-[OrderID], [Status]=Deleted=14 WHERE [PayID]` (18996; código muerto para Certificate, 5.4). Una propina de CFD (`mValues.Object4`) se revierte de `Orders.Total` **solo con eGift** (`Orders.Total=@Total-@Tip`, 19028) y su fila de `OrdersTips` se niega (`OrderID=-OrderID`, 19040); `Orders.Status=Pending=6` cuando ya se había pagado algo y hay una fila Completed en memoria (19058); en las formas de pago con descuento o comisión se reescriben `Orders.TDUsed/TFUsed/Tax/Total` (19093). Las filas de Cash **no** se borran (se quedan en `Status=0` con un PayID); los puntos de fidelización consumidos en 17907 no se restauran nunca.

### 5.10 Completar más tarde un pago autorizado (`Auth=6`)

Las ventas con tarjeta en restaurante dejan `Payments.Status=6`, `CCTxs.Status=0/TxType=0` mientras `Orders.Status=1`. Se capturan por: la **ventana Complete** — `Complete(object)` 1061 → `btnCompleteClick` 13429 → `frmComplete` → `_OnComplete(pOrderID,pPayID,pTip)` (9230-9435; los candidatos = Payments JOIN CCTxs con `CCTxs.Status=0`, los procesadores semiintegrados exigen `TID` NULL o el de este terminal, y los pedidos de Delivery exigen `OrdersDelivery.Status` Out/Delivered) → `Global.Processors.Complete(PayID, null)` (9324; escrituras de csPayment: captura en CCTxs 483-491; Payments `Status=1, Amount+=tip, CloseUserID=Orders.StaffID`; Orders `Paid/Tip/Total += tip` 495-509; recargo 517-532; OrdersTips 547-561; segunda suma con TouchScreen 565-569; FeeFromTips 575-579); después, en los pedidos de Delivery, `UPDATE [OrdersDelivery] SET [Delivered]=GETDATE(), [Status]=3 WHERE [ODELID]` (9338), y la limpieza del plano de salón (9348-9432: `DELETE [OrdersFPSplit]` + `FloorPlan SET OrderID=NULL, InUse=0, LastAct=NULL, Status=6` cuando no hay ningún control en pantalla, 9360-9424). La rama de crédito del **gestor de reparto** (frmDelManager.cs:659 → `Complete(PayID, tip)` con una propina decimal, así que las escrituras de importe de Payments/Orders 495-509 se **omiten**; frmDelManager ejecuta luego el batch H `UPDATE [Orders] SET [Tip]+=@Tip,[Paid]+=@Tip,[Total]+=@Tip; UPDATE [Payments] SET [Amount]+=@Tip,[CloseUserID]=Global.UserID,[Status]=1`, 663-681). **Batch Close** (en esta compilación, csPayment.cs:165-193, sin filtro de terminal ni de estado): `UPDATE [Payments] SET [CloseUserID]=Global.UserID or [UserID], [CloseDate]=ISNULL([CloseDate],@Now), [Status]=IIF([Status]=6,1,[Status]) FROM Payments JOIN CCTxs … WHERE BatchClose IS NULL AND Number IS NULL; UPDATE [CCTxs] SET [BatchClose]=@Now, [CloseDate]=IIF(Status=0,@Now,CloseDate), [Status]=IIF(Status=0,1,Status), [TxType]=IIF(TxType=0,1,TxType) WHERE BatchClose IS NULL AND Number IS NULL` — las filas TabAuth también se sellan y su TxType pasa a Capture, tras lo cual `Complete` las rechaza (330) mientras que `VoidAuth` sigue funcionando. **Pay-at-table** (`spApplyPayment`) es una ruta de dispositivo aparte (sección 8).

### 5.11 Lista de escrituras de `btnProcessClick`, en orden de ejecución

| # | archivo:línea | tabla | escritura | condición / ejecución |
|---|---|---|---|---|
| A1 | 739 via 16385/16543/16675 | Orders | `InUseID=NULL` | salidas: tab en uso en otro sitio; register bloqueado sin artículos (raw) |
| A2 | 7784 via 16345 | Customers | `ClubNumber` | ruta ReqCustomer (wrapper) |
| A3 | SaveOrder via 16371/16530/16637 | todas | sección 3.12 | salidas por `Modified` / tab en otro sitio |
| A4-A6 | 16572 / 16589 / 16619 | Payments; Orders | precálculo del tab (5.2) | Tab Complete, `!Modified` (raw) |
| A7 | PaymentManager.cs:25 via 16730 | Payments | `Tip` | fila PayOutCust (Dapper) |
| A8 | 16794 | Orders; OrdersTips | `Total+=tip`; INSERT Type=7 | propina de CFD (lote wrapper) |
| A9 | csDualPriceManager.cs:278 via 16858 | Payments | `TFUsed` | Cash, precio dual después de tarjeta (raw) |
| A10 | csPayment.cs:483-579 via 16915 | CCTxs; Payments; Orders; OrdersTips | captura (5.4 Tab Complete) | wrapper por sentencia |
| A11 | 16956 | Orders; Payments | TD/TF a cero | Tab Complete fallido (lote raw) |
| A12-A13 | 17014 / 17024 | Payments; Orders | `TFUsed` | tab complete correcto, comisión por forma de pago (raw) |
| A14 | 17061 | Payments; CCTxs; Orders | recargo | tab complete correcto, recargo (lote raw) |
| A15 | 17085 | Orders | `TabAuthID=NULL` | tras una captura correcta (raw) |
| A16 | 17097 | Payments | `Type=3` | tab abierto offline (raw) |
| A17 | csPayment.cs:94,113 via 17235 | CCTxs; Payments | SaveTrx offline | wrapper |
| A18 | 17237 | CCTxs; Payments; Terminals | `Number`; `Type=11`; `OffDate` | offline (lote raw; lanza si A17 falló) |
| A19 | csPayment.cs:94,113 via 17330 | CCTxs; Payments (+CCTxFailures) | SaveTrx online | aprobado por el dispositivo |
| A20 | csDualPriceManager.cs:278/301 via 17379-17380 | Payments; Orders | `TFUsed`; `Total` | precio dual (raw) |
| A21 | 17395 | Payments | `Amount=AuthAmount` | cancelación parcial (raw) |
| A22 | csPayment.cs:1105-1176 via 17403 | CCTxs; Payments; OrdersFPSplit; Terminals | VoidAuth (`pVoidOrder=true`) | cancelación parcial |
| A23 | SaveOrder via 17423 | todas | TD/TF recalculados | cancelación parcial con comisiones |
| A24 | 17432 | CCTxs | captura | parcial aceptada (raw) |
| A25 | 17454 | Terminals | `AuthDate=GETDATE(), Sent=NULL` | Credit online, primera vez (raw) |
| A26 | 17486 | CCTxs; Orders; Payments | estados de tab auth | Tab Auth (lote raw) |
| A27 | 17521 / csObject.cs:472 via 17532 | FloorPlan | `Status=7` | Tab Auth, plano de salón |
| A28 | 739 via 17537 | Orders | `InUseID=NULL` | Tab Auth creada (raw) |
| A29 | 17554 | Orders; CCTxs; OrdersTips | propina en el terminal Type=5 | TipReq (lote wrapper) |
| B1 | csDualPriceManager.cs:278 via 17615 | Payments | `TFUsed` | OnAccount, precio dual |
| B2 | csPayment.cs:94-118 via 17726 | CCTxs; Payments | SaveTrx de eGift | eGift canjeada |
| B3 | GiftCardManager.cs:116 via 17732 | GiftCards | `Balance-=`, `Used`, `UsedDate` | eGift con fila local (Dapper) |
| B4 | CardTransactionManager.cs:28 via 17735 | CCTxs | `Number` | eGift (Dapper, fila completa) |
| B5 | csDualPriceManager.cs:278 via 17744/17836 | Payments | `TFUsed` | precio dual de eGift/Certificate |
| B6 | GiftCardManager.cs:116 via 17829 | GiftCards | `Balance-=amount` | Certificate (Dapper) |
| B7 | 17908 | Loyalty | `Used` | Reward EPI (wrapper por fila) |
| B8-B10 | 17938 / 17959 / 18008 | Payments; Orders; Payments | batch A (5.5) | cada forma de pago (wrapper, traga) |
| B11 | csDualPriceManager.cs:278/301 via 18124-18125 | Payments; Orders | `TFUsed`; `Total` | cash/check DP en modo no táctil |
| B12-B14 | 18352 / 18361 / 18382 | Payments; Orders+Payments; BankMod | cash-back (5.6) | wrapper / raw / raw |
| B15-B19 | csDualPriceManager.cs:690/721/773/823/878/923/995 via 18485-18531 | Orders; OrdersDetails; OrdersTax; OpenPromoDetails; OrdersDelivery; Payments | reescritura DP (5.7) | raw / registrado en el log |
| B20-B21 | csPayment.cs:1105, 1173 via 18572 | CCTxs; Payments; Orders; Terminals | anular la preautorización del tab sobrante | wrapper |
| B22-B23 | 18623-18644 | OrdersTimeCharge; OrdersLog; OrdersPromoDetails; OpenPromoDetails; Orders | batch C (5.8) | hilo en segundo plano, wrapper |
| B24-B26 | 18713 / 18729 / 18741 | Loyalty; Customers; Customers | acumulación; club; visitas | con cliente asignado (raw/raw/wrapper) |
| B27-B28 | GiftCardManager.cs:393 via 18816; OrderManager.cs:232 via 18824 | GiftCards; OrdersDetails | certificado vendido | Dapper |
| B29-B31 | 18868 / 18877+18895 / csObject.cs:472 via 18890-18923 | OrdersFPSplit; FloorPlan | liberación del plano de salón | raw / wrapper |
| B32 | 739 | Orders | `InUseID=NULL` | hilo de interfaz, concurrente con B22 (raw) |
| B33-B36 | 18996 / 19028+19040 / 19058 / 19093 | Payments; Orders+OrdersTips; Orders; Orders | WithError (5.9) | raw |
| B37-B41 | csPayment.cs:502/527/548 via 9324; 9338; 9360; 9377; 9401/9414 | Orders; OrdersTips; OrdersDelivery; OrdersFPSplit; FloorPlan | `_OnComplete` (5.10) | raw |

## 6. Anulaciones

Cuatro mecanismos distintos anulan cosas; tocan filas diferentes y solo uno es transaccional.

| Mecanismo | Se llega desde | Qué anula | Transacción |
|---|---|---|---|
| `OrderManager.VoidOrder` (OrderManager.cs:56-221) | **solo** frmRegister.cs:18791-18797, vía `BeginInvoke`, cuando la línea eGift de un pedido recién completado falló en `GiftCardManager.Activate` (`CanceledByUser`) | el pedido completado entero: pagos, transacciones de tarjeta, stock, pedido, plano de salón, reemisión del certificado | `XTUnitOfWork(true)` — sí |
| `csPayment.VoidAuth(pPayID, pCheckGift=true, pVoidOrder=false)` (csPayment.cs:961-1186) | Void de frmComplete (frmComplete.cs:257) y Void de frmReView (frmReView.cs:213) con `(true,false)`; Join en el plano de salón de una mesa TabAuth (14918, `(true,false)`); cancelación de una aprobación parcial (17403, `(true,true)`); pedido cerrado con otra forma de pago mientras hay una preautorización de tab abierta (18572, `(false,true)`); filas de tarjeta de frmRefund en modo Void o pagos Auth (frmRefund.cs:2250, `(true,true)`) | un pago con tarjeta o tarjeta de regalo (y, con `pVoidOrder=false`, el `Paid/Status/Tip/Total` del pedido y las propinas de ese pago) | no — un lote, wrapper |
| **Void** de frmGetOrders (frmGetOrders.cs:384-484) | Register → Get Order → seleccionar un pedido **Open** (`Status=0 AND TabAuthID IS NULL`) → Void (seguridad `Register.VoidOrder`; diálogo de motivo cuando `RefReqReason`, si no `ReasonID=40`) | un pedido abierto, sin pagar o pagado en parte: sin llamada al procesador, los pagos Open se marcan como Deleted | no — un lote, wrapper ExecReader |
| **Void** de frmRefund (frmRefund.cs:1875-3034, `mReversalType=Void`) | Register → Refund → cargar un pedido completado → Void (`_Ref==_Total`, ningún pago Debit/EBT, ninguna tarjeta con el lote ya cerrado, `RefundAmt==0`, seguridad `Register.VoidOrder`) | un pedido completado y pagado: anulación en el procesador por cada pago con tarjeta, `Orders.Status=5`, Re-create opcional | no — sentencias raw |

A esto se suman las rutas de reversión que no se llaman "void": la liquidación rechazada de `frmOfflineProcess` (sección 9.9) y el `Payments.Status=Voided=9` que `Lib.JoinOrder` aplica a las filas ClubDiscount de un origen unido (Lib.cs:4944).

### 6.1 `OrderManager.VoidOrder` (transaccional)

Paso 1: `new XTUnitOfWork(true)` (64). Paso 2: `SELECT * FROM Orders WHERE OrderID=@Id`; `SELECT * FROM Payments WHERE [OrderID]=@OrderId AND [Amount]>0 AND ([Status]=1 OR [Status]=6)` (PaymentManager.cs:41-48; las filas TabAuth=11 no se seleccionan nunca, así que aquí nunca se limpia `Orders.TabAuthID` — rama muerta 171-172; sin ORDER BY). Paso 3, por cada pago (70-157): Credit=3/Debit=4/EBTCash=18/EBTFood=6 → `PaymentManager.VoidCardPayment` (PaymentManager.cs:54-141): lanza si `Payments.CCTxID` es NULL, provoca una NRE si falta la fila de CCTxs, devuelve **false y sin llamar al procesador cuando `!Global.IsConnected`** (65-68); `AType=GiftCard=4` → `GiftCardManager.AddValue` por HTTP; si no, `Process(CardData{TxType=Void, Amount=Payment.Amount (Value si es 0), Tip=CCTxs.Tip, AuthCode, RefCode=PNRef, TransactionID=TransmitID})`. La comprobación de aborto (82-85) lanza `VoidPaymentException` solo cuando el tipo no es Offline Y la anulación devolvió false Y `Processors.Default.ResponseMessage` no está en blanco Y no es "already voided" — de modo que un terminal offline, un procesador sin inicializar o un AddValue de tarjeta de regalo fallido dan como resultado una **anulación solo local a la que se hace commit**. Offline=11 se salta el procesador. Los pagos eGift=7 lanzan (`AllowGiftCardPayments=false`, 136-137); Factor4Loyalty=17 necesita `Orders.CustomerID` y llama al reembolso SOAP de Factor4; Check/Coupon/Discount/PayIn/OnAccount/CardOnFile solo se marcan como Voided (sin reversión en el CRM para OnAccount). Paso 4 (159-165): cada fila de `OrdersDetails` con `ItemID` y `Qty` no NULL (incluidas las filas Deleted, **sin filtro de StockType**, e incluidos los pseudo-artículos 1-12 y los modificadores) → `ItemRepository.UpdateQtyOnHand`: `QtyOnHand += Qty` y después un UPDATE de fila completa de `Items` con 141 columnas (ItemRepository.cs:34-40; lo que era NULL sigue NULL; si falta la fila → NRE → rollback). Paso 5: UPDATE de fila completa de `Orders` con `Status=Voided=5, RefundAmt=Paid (tal como se leyó en el paso 2), VoidDate=DateTime.Now` (167-174; todas las demás columnas se reescriben a partir de la instantánea del paso 2). Paso 6: con `License.FloorPlanEnabled` y `Orders.FPID`, si `FloorPlan.OrderID` coincide y `Status<>Split=10`: UPDATE de fila completa con `Status=CleanUp=6` (`OrderID` **no** se limpia, 176-183). Paso 7: `mCertVoided>0` (importes Certificate=12 / Reward=10) → `GiftCardManager.CreateGiftCertificate` sobre la misma UoW: `INSERT INTO [GiftCards] (CustomerID=Orders.CustomerID, OrderID, UserID, Amt=Balance=Σ, DateSold=now, FromRefund=1, Used=0, GiftCode='3'+8 dígitos, …); SELECT SCOPE_IDENTITY()` (187-196). Paso 8: `Commit` (198). Tras el commit: cajón de dinero cuando `OpenAlwaysCD` o cuando se anuló efectivo, `Lib.Success("Transaction voided" …, pAutomaticClose=false)` — **la automatización debe descartarlo** (210), mensaje al CFD, `Print.GiftCert`, `Print.Refund(OrderId)`.

| # | archivo:línea | tabla | escritura | condición |
|---|---|---|---|---|
| V1 | OrderManager.cs:108 | CCTxs | fila completa: `RAmt=Payments.Amount, Status=3, Tip=0` (+`Number=NULL, OffLineStatus=4` para Offline) | por cada pago con tarjeta/EBT/Offline, tras el paso del procesador |
| V2 | OrderManager.cs:120 | Terminals | fila completa: `OffDate=NULL, OffSent=NULL`; `NoOffline(false)` | después de V1, cuando ninguna fila de `CCTxs` de este terminal tiene `Number IS NOT NULL` (cualquier fila eGift histórica lo bloquea) |
| V3 | OrderManager.cs:156 | Payments | fila completa: `Status=9, RefundAmt=Amount, CloseDate=now` (`CloseUserID, BankID, Tip` sin cambios) | cada pago seleccionado |
| V4 | ItemRepository.cs:39 vía OrderManager.cs:164 | Items | fila completa: `QtyOnHand += Qty` | por cada fila de detalle con ItemID y Qty |
| V5 | OrderManager.cs:174 | Orders | fila completa: `Status=5, RefundAmt=Paid, VoidDate=now` | siempre |
| V6 | OrderManager.cs:183 | FloorPlan | fila completa: `Status=6` | plano de salón, FPID, OrderID coincide, no es Split |
| V7 | GiftCardManager.cs:393 vía OrderManager.cs:189 | GiftCards | INSERT `FromRefund=1` | `mCertVoided>0` |
| V8 | OrderManager.cs:198 | — | COMMIT; cualquier excepción anterior hace rollback de V1-V7 (las anulaciones en el procesador, los abonos a tarjetas de regalo, el reembolso de Factor4 y las filas de `CcTxFailures` **no** se revierten) | |

**Ausente**: ningún cambio en `OrdersLog`, `OrdersTips`, `OrdersFPSplit`, `OrdersTax` ni `Customers`; ninguna difusión por socket; `Orders.ReasonID/CloseDate/Note/InUseID` y `Payments.CloseUserID` quedan intactos. Por el `BeginInvoke`, la anulación se ejecuta después de que `btnProcessClick` haya retornado; sus excepciones afloran como un diálogo `Lib.Error` a través de `Application.ThreadException` (Start.cs:233-253).

### 6.2 `csPayment.VoidAuth` (un solo pago)

Lee `Payments`+`CCTxs` (csPayment.cs:964-973): `Amount = Payments.Amount` o `Value` cuando es 0 (979), `Tip = CCTxs.Tip` (981), `IsTabAuth = Payments.Status=11` (980), offline = `Payments.Type=11` (985). El camino online necesita `Global.IsConnected` (991). Con `pCheckGift`: no hay escrituras si el pedido tiene un certificado de regalo ya usado (1015) o una línea eGift con saldo por debajo de `PriceSell` (1021-1048). Si no es tarjeta de regalo: Void en el procesador (1066-1082; en Dejavoo el TabAuth captura primero, 1058-1064); si lo es: `GiftCardManager.AddValue` (1094). El caso offline va directo al SQL (986-987). Devuelve false **sin mostrar mensaje** cuando no hay conexión, en las comprobaciones de tarjeta de regalo, si falla `Init` o si falla AddValue — los llamadores que solo comprueban el booleano persisten entonces su propio estado "voided" sin ninguna anulación en el procesador (peligro de frmRefund.cs:2255). Después, un único lote vía `ExecScalar` (1105-1173):

```sql
DECLARE @TabID INT;
UPDATE [CCTxs] SET [Status]=3, [Tip]=0, [TxType]=6 /* , [Number]=NULL, [OffLineStatus]=4 cuando es offline */ WHERE [CCTxID]=@CCTxID;
UPDATE [Payments] SET @TabID=IIF([Status]=11,[OrderID],NULL), [Amount]=IIF([Status]=11,[Value],[Amount]), [CloseDate]=GETDATE(), [Status]=9 WHERE [PayID]=@PayID;
IF (@TabID IS NULL) BEGIN
    UPDATE [OrdersFPSplit] SET [Status]=3 /* Ordered */ WHERE [OrderID]=@OrderID;
    SELECT @Count=COUNT(1) FROM [Payments] WHERE [OrderID]=@OrderID AND [Status] NOT IN (14, 8, 9);
    /* solo cuando pVoidOrder=false: */
    UPDATE [Orders] SET [Paid]=[Paid]-@Amount, [Status]=IIF([Status] IN (1,6), IIF(@Count=0, 0, 6), [Status]), [Tip]=[Tip]-@Tip, [Total]=[Total]-@Tip WHERE [OrderID]=@OrderID;
    DELETE FROM [OrdersTips] WHERE [PayID]=@PayID;
END ELSE UPDATE [Orders] SET [TabAuthID]=NULL WHERE [OrderID]=@TabID;
```

Después, siempre que `ExecScalar` haya devuelto null — **siempre** en las anulaciones online (el lote no tiene resultado de SELECT) y en las offline solo cuando no queda ninguna fila de `CCTxs` con `Number` (1151-1154) — `UPDATE [Terminals] SET [OffDate]=NULL, [OffSent]=NULL WHERE [TerminalID]=Global.TerminalID` y `NoOffline(false)` (1173-1181). Efecto neto según el llamador: **Void de frmComplete/frmReView** (`pVoidOrder=false`, sin tab): CCTxs 3/6, Payments 9, `Orders.Paid-=Amount`, `Status` 1/6 → 0 cuando no queda ningún pago vivo y 6 en caso contrario, `Tip/Total -= CCTxs.Tip`, se borran los `OrdersTips` de ese pago, `OrdersFPSplit.Status=3`; frmComplete ejecuta luego `RevertAnyGiftCardOrCertificate` para los pagos Auth de restaurante (frmComplete.cs:263: `GiftCards SET Balance=0, Used=1, UsedDate` para las líneas de certificado). **Filas de tab** (Status 11 → `@TabID` asignado): solo CCTxs 3/6, Payments 9 con `Amount=Value`, `Orders.TabAuthID=NULL`; `Paid/Status/OrdersTips` quedan intactos (el Join de 14918 y el de 18572 dependen de ello). **`pVoidOrder=true`** (17403, 18572, frmRefund): no se emiten ni el rollback de Orders ni el DELETE de OrdersTips; la fila del pedido es cosa del llamador.

### 6.3 Void de frmGetOrders sobre un pedido abierto (frmGetOrders.cs:384-484)

Solo se habilita para las filas con `Status=Open=0 AND TabAuthID IS NULL` (521), nunca en el modo de pedidos Completed (546). Sin llamada al procesador. Un único CommandText vía `ExecReader(SingleRow)` (444):

```sql
UPDATE [Items] SET [QtyOnHand]=[QtyOnHand]+[Qty] FROM [Items] JOIN (SELECT [ItemID], SUM([Qty]) AS [Qty] FROM [OrdersDetails] WHERE [OrderID]=@OrderID AND [Deleted]=0 GROUP BY [ItemID]) [TEMP] ON … WHERE [StockType]<>2;   -- QtySold sin cambios
INSERT INTO [OrdersLog] ([OrderID],[ID],[Type]) VALUES (@OrderID, @UserID /* Global.UserID */, 6 /* Voided */);
INSERT INTO [OrdersLog] ([OrderID],[ID],[Type]) VALUES (@OrderID, @ManagerID /* Global.MgrID: NULL sin override en Release, 1 en esta compilación */, 2 /* Manager */);
UPDATE [Payments] SET [Status]=14 WHERE [OrderID]=@OrderID AND [Status]=0;
UPDATE [Orders] SET [ReasonID]=@ReasonID, [CloseDate]=GETDATE(), [Note]=@Note+CAST(ISNULL([Note],'') AS VARCHAR(MAX)), [Status]=5 OUTPUT INSERTED.[CustomerID], INSERTED.[FPID] WHERE [OrderID]=@OrderID;
```

Un `@Note` NULL (sin diálogo de motivo) borra cualquier nota existente. `VoidDate`, `RefundAmt`, `BatchID` e `InUseID` quedan intactos; también los pagos que ya estaban Completed (aquí no se puede anular un pedido Pending pagado en parte, pero una fila Completed sobre un pedido Open conservaría Status 1). Con un `FPID`: las mesas divididas pierden su fila de `OrdersFPSplit` (`DELETE … WHERE [OrderID]; COUNT WHERE [FPID]`, 462-468) y, cuando no queda ninguna fila de división o la mesa no estaba dividida, `UPDATE [FloorPlan] SET [Status]=6, [LastAct]=GETDATE(), [OrderID]=NULL WHERE [FPID]` (474-478) más `OnUpdate` → `frmRegister._OnUpdate` (9477-9481, `UpdateFP` + difusión por socket). El diálogo devuelve `Abort` → el `Clear()` de frmRegister (14038-14042) pone a NULL el `InUseID` de **cualquiera que sea el pedido que el register tuviera cargado**, no el del pedido anulado.

### 6.4 Void de frmRefund sobre un pedido completado (frmRefund.cs:1875-3034)

Precondiciones: `mCanVoid` = el `RefundAmt==0` original (469), ningún pago Debit/EBT (566-580), ningún pago con tarjeta con `CCTxs.BatchClose` puesto (597), ninguna línea Certificate/GiftCard/Club no reembolsable (694-714); solo se habilita cuando `_Ref==_Total` (1544); los pagos con tarjeta tienen que ser de este terminal (1913-1914); cada fila recibe `RefAmt=Amount` (1916); el diálogo de `ReasonID` solo cuando `RefReqReason`, si no 40 (1951) — y **el motivo no se persiste nunca** (la escritura 13 no lleva ReasonID/Note). Por cada pago con tarjeta (Credit/Debit/EBT): `Global.Processors.VoidAuth(PayID, true, true)` (2250, TxType Void; su lote hace CCTxs 3/6, Payments 9/`CloseDate`, `OrdersFPSplit.Status=3` y `Orders.TabAuthID=NULL` en los tabs; se omiten Orders/OrdersTips). Cash/Certificate/eGift/OnAccount se acumulan para el cajón de dinero, el certificado y el CRM; los tipos que no tienen `case` (CardOnFile, DeliveryService, ClubDiscount, PayIn/PayOut y Offline en modo Void) no generan ninguna llamada al procesador. Después: stock `UPDATE [Items] SET [QtyOnHand]=[QtyOnHand]+@QtyToRefund WHERE [ItemID] AND [StockType]<>2` por cada línea cargada (2648-2654, raw, se ignora el flag Refund, `QtySold` sin tocar); las líneas Certificate/GiftCard → `GiftCards SET Balance=0, Used=1, UsedDate` (GiftCardManager.cs:149-167 vía 2662) y `Redeem` del proveedor; **ningún** pedido de reembolso, ningún cambio en OrdersDetails/OrdersTax/OrdersTips/OrdersLog. Sobre el pedido original: `UPDATE [Orders] SET [Status]=5, [RefundAmt]=ISNULL([RefundAmt],0)+_Ref, [VoidDate]=GETDATE() WHERE [OrderID]` (2706-2733; `CloseDate/ReasonID/Note` intactos; el CleanUp de FloorPlan de la línea 2749 es código muerto — `@FPID` no se asigna nunca). Por cada pago anulado: `UPDATE [Payments] SET [RefundAmt]=ISNULL([RefundAmt],0)+@RefAmt, [Status]=9 WHERE [PayID]` (2817-2819) junto con `UPDATE [CCTxs] SET [RAmt]=@RefAmt, [Status]=3 [, Number=NULL, OffLineStatus=4 para Offline] WHERE [CCTxID]` (2823-2832); siempre que ese lote no devuelve ninguna fila (todas las filas no offline), `UPDATE [Terminals] SET [OffDate]=NULL, [OffSent]=NULL` (2846-2850). `mCert>0` → un certificado nuevo en `GiftCards` (`FromRefund=1`, GiftCardManager.cs:370-402 vía 2906-2913, con `OrderID`= el del pedido original en el caso Void). Las propinas y `Paid` se dejan como están. Diálogo de éxito (3018-3023); a continuación, o bien `_Clear()` → `UPDATE [Orders] SET [InUseID]=NULL` (222, 3033), o bien, con **Re-create** marcado, las escrituras 20-27 de la sección 7.5 y `Global.Register.SearchOrder(newID)` — con lo que el `InUseID` del original **no se libera nunca** (no hay manejador FormClosing).

### 6.5 Comparación fila a fila

| Efecto | VoidOrder (6.1) | VoidAuth `pVoidOrder=false` (6.2) | Void de la lista de recuperación (6.3) | Void de frmRefund (6.4) |
|---|---|---|---|---|
| `Orders.Status` | 1 → 5 | 1/6 → 0 o 6 (solo ese pago) | 0 → 5 | 1 → 5 |
| `Orders.RefundAmt` / `VoidDate` / `CloseDate` / `ReasonID` | `=Paid` / ahora / — / — | — | — / — / GETDATE() / 40 o el motivo | `+=_Ref` / GETDATE() / — / — |
| `Orders.Paid` | sin cambios | `-=Amount` | sin cambios | sin cambios |
| `Payments.Status` | 1/6 → 9, `RefundAmt=Amount`, `CloseDate` | → 9, `CloseDate`, en tab `Amount=Value` | 0 → 14 | 1/6 → 9, `RefundAmt+=Amount` (CloseDate vía VoidAuth en las tarjetas) |
| `CCTxs` | `Status=3, RAmt, Tip=0` | `Status=3, TxType=6, Tip=0` | intacto | `Status=3, TxType=6, Tip=0` y luego `RAmt` |
| `OrdersTips` | intacto | se borran las filas de ese PayID; `Orders.Tip/Total -= CCTxs.Tip` | intacto | intacto |
| `Items.QtyOnHand` | `+= Qty` en todas las líneas, incl. pseudo-artículos, sin filtro de StockType | — | `+= SUM(Qty)` de las no borradas, `StockType<>2` | `+= QtyToRefund` por línea, `StockType<>2` |
| `OrdersLog` | ninguno | ninguno | (6, UserID), (2, MgrID) | ninguno |
| FloorPlan / OrdersFPSplit | `Status=6` (se conserva OrderID) | `OrdersFPSplit.Status=3` | se borra la fila de `OrdersFPSplit`; `Status=6, OrderID=NULL` | ninguno (código muerto) |
| Terminals | `OffDate/OffSent=NULL` cuando no quedan filas offline | `OffDate/OffSent=NULL` siempre (online) | — | `OffDate/OffSent=NULL` por cada fila no offline |
| GiftCards | se reemite el certificado para las formas de pago Certificate/Reward | frmComplete: se cancelan las líneas de certificado | — | se cancelan las líneas de certificado y de tarjeta; se emite un certificado por `mCert` |
| Procesador | Void por cada tarjeta (o solo local) | Void / AddValue | ninguno | Void por cada tarjeta |
| Atómico | sí | no (un lote) | no (un lote) | no |

## 7. Reembolsos: `frmRefund`

### 7.1 Apertura, carga y bloqueo

Register → Refund (`btnRefundClick` 19277-19289: `_CheckForSave()`, `Clear()` libera el pedido de Register, `Lib.ShowForm(typeof(frmRefund))` no modal, sin pasar id de pedido); código de barras `'G'` escaneado en el formulario principal (frmMain.cs:2598-2608); Delivery → Manage (`new frmRefund(true)` modal, frmManage.cs:28-35). **Find Order** (`frmSearch(true)`) es el único sitio donde se filtra la elegibilidad: `[O].[Status] IN (1, 7, 6) AND [O].[Paid] > ISNULL([O].[RefundAmt],0)` más `DATEDIFF(DAY, ISNULL(CloseDate,CDate), @Today) <= @RefDays` cuando `Refund.RefDays<>0` (frmSearch.cs:69-81). `_LoadOrder` (frmRefund.cs:315-826) **no aplica ningún filtro por Status**; rechaza: pedido inexistente (354); `InUseID` en poder de otro terminal (365-375, retorna **sin** `_Clear()`, dejando `MainID` asignado); `Paid<=0` (382); ningún pago `Completed=1`/`Auth=6` (474-522); una línea de certificado ya usada (678-684). La comprobación de RefDays en 394-401 nunca salta para pedidos anteriores. Los pagos se leen con `Amount = ROUND([Amount]-ISNULL([RefundAmt],0),2)` (lo que queda por reembolsar); las filas Coupon/Discount se ocultan; Check/CheckCashBack/PayOutCust se re-tipifican a Cash en memoria (556-607); `mCanVoid` se limpia para Debit/EBT o para cualquier tarjeta con el lote ya cerrado; `_CanRefund=false` si algún pago es Offline=11 o Auth=6 (605-606). Detalles: `[Qty]-ISNULL([RefQty],0) AS QtyToRefund WHERE [Deleted]=0 AND [Qty]>ISNULL([RefQty],0)` (638-649) — las líneas totalmente reembolsadas desaparecen. Luego el bloqueo: `UPDATE [Orders] SET [InUseID]=@InUseID (Global.TerminalID) WHERE [OrderID]` solo si era NULL (722-729, raw). `_Remain = Paid − RefundAmt` (467). Clear/Cancel/Find-Order-OK/reentrada en `LoadOrder` ejecutan `UPDATE [Orders] SET [InUseID]=NULL WHERE [OrderID]=MainID` (222/1571) — con lo que sea que contenga `MainID`, así que tras una carga rechazada pueden borrar el bloqueo de **otro terminal**. No existe manejador `FormClosing`: cerrar el formulario por cualquier otra vía deja `InUseID` obsoleto.

### 7.2 Modo Refund: validación y movimiento de dinero (1890-2176)

`btnRefund.Enabled = _CanRefund && _Ref<>0 && _Paid>=_Ref && !chkReCreate.Checked` (1543, 3084); seguridad `Register.Refund` (858). El diálogo de motivo solo aparece cuando no es Pending y `RefReqReason`; en caso contrario `_ReasonID=39` (1951). Las líneas GiftCard marcadas pasan por `GiftCardManager.EnsureReturnability` (1924-1927, HTTP). El importe se reparte automáticamente en FIFO sobre la vista de pagos (1992-2009), o bien `frmRefundTool` toma el `RefAmt` de cada pago / reescribe el Type de todos los pagos visibles a Certificate=12 ("Gift Cert") o eGift=7 ("Gift Card" — llama **de inmediato** al `Activate(Redeem=_Ref)` del proveedor, 2044). **Bucle de pagos** (2178-2434): por cada pago visible con `RefAmt<>0`, `RefTD/RefTF/RefSurcharge` se calculan solo para `Type<14` (2202-2228); `mVoidPayment = Void mode || Status==Auth=6` (2231). Tipos de tarjeta (3/4/18/6): `VoidAuth(PayID,true,true)` (2250) cuando `mVoidPayment`; si no, `CardData{TxType=Return=5, Amount=RefAmt, OCCNumber, RefCode=PNRef, Surcharge, PayID=DBNull}` (2264-2277) — offline: tarjeta manual, AuthCode falso `'xxOxL'`, `SaveTrx` (2308: CCTxs `TxType=5, Status=1, TxRespons='Offline'`, sin actualizar Payments porque `PayID` es DBNull) + lote `UPDATE [CCTxs] SET [Number]=RSA(...); UPDATE [Terminals] SET [OffDate]=GETDATE(),[OffSent]=NULL WHERE [OffDate] IS NULL` (2310-2323), fila re-tipificada a Offline; online: `Process` (2349, inserta la fila Return de CCTxs vía SaveTrx, rechazo → `CCTxFailures`) y después `UPDATE [CCTxs] SET [Tip]=-(tip)` para la fila de propina seleccionada que corresponda (2360-2367). Cash → `mCash`; Certificate/Reward → `mCert`; eGift → `mGift`; OnAccount → `mOnAccount`; Factor4 → reembolso del proveedor (2375-2397). Una fila procesada recibe en memoria `Status` Voided=9 / Refunded=8 y se suma a `mPayRows` (2427-2431). **El dinero se mueve antes de escribir ninguna fila local**; una `SqlException` en 2360-2367 ofrece Retry (un segundo Return contra el procesador) / Issue Certificate / Cancel (2406-2424).

### 7.3 Persistencia del reembolso: el pedido de reembolso (2438-2634, modo Refund, original no Pending)

| # | archivo:línea | tabla | escritura | ejecución |
|---|---|---|---|---|
| 3 | 2448-2481 | Orders (pedido de reembolso) | INSERT: `CustomerID, CustomID, OtherID, RefID=MainID (NULL si _FromItem), ReasonID=_ReasonID, StaffID (_FromItem ? Global.UserID : el original), TaxID, TerminalID=Global.TerminalID, BaseAmount=-Round(_RefBaseAmount,2), CloseDate=GETDATE(), DeliveryFee=+_DeliveryFee si chkDelivery, si no NULL (sin negar), Note, OrderType=_OrderType, OtherAmount=-_OtherAmount si chkOther, si no NULL, Paid=-_Ref, Status=Refunded=4, Tax=-_RefTax, TDUsed=-…, TFUsed=-…, Tip=-_RefTip, Total=-_Ref, Surcharge=**+_RefSurcharge** (doble negación: la lista VALUES usa `-@Surcharge` y el parámetro se enlaza como `-_RefSurcharge`, 2451/2466 — la única columna de dinero del pedido de reembolso que conserva el signo del original); SELECT SCOPE_IDENTITY()` → `mOrderID` | wrapper ExecScalar |
| 4 | 2484-2507 | OrdersLog ×3 (+OrdersDelivery) | `(mOrderID, Global.UserID, Create=0)`, `(…, Close=1)`, `(mOrderID, Global.MgrID, Manager=2)`; los pedidos Delivery copian la fila `OrdersDelivery` | lote raw |
| 5 | 2514-2522 | OrdersTax | por cada ranura con valor: `Qty=-_RefValue[i], QtyExempt=-_RefExValue[i], Type=i, CDate=DateTime.Now` | raw por ranura |
| 6 | 2530-2548 | OrdersTips | por cada propina seleccionada cuando `_RefTip<>0`: `UPDATE SET [RefAmount]=[Amount] WHERE [TipID]`; `INSERT (OrderID=mOrderID, StaffID, Amount=-Amount, Type)` | lote raw por propina |
| 7 | 2562 | Items | `QtyOnHand += QtyToRefund WHERE ItemID AND StockType<>2` (`QtySold` sin tocar) | mismo CommandText que 8 o que 9+10 (raw, 2612) |
| 8 | 2566-2570 | OrdersDetails (pedido de reembolso) | solo `_FromItem`: INSERT…SELECT de 44 columnas desde `Items JOIN ItemsSKUs`, `Qty=-QtyToRefund, RefQty=+QtyToRefund`, precios tomados de la cuadrícula | con 7 |
| 9 | 2572-2574 | OrdersDetails (línea original) | `UPDATE SET [RefQty]=ISNULL([RefQty],0)+@QtyToRefund WHERE [ODID]` | con 7, 10 |
| 10 | 2576-2580 | OrdersDetails (pedido de reembolso) | copia de 65 columnas del ODID con `OrderID=mOrderID, Qty=-QtyToRefund, Modifier=IIF(DRV IS NULL OR DRV=0, Modifier, 0)`, `RefQty` a NULL, `StaffID/UserID` originales | con 7, 9 |
| G0 | GiftCardManager.cs:149-167 vía 2616 | GiftCards | `Balance=0, Used=1, UsedDate` para una línea Certificate=5/GiftCard=6 reembolsada (`GiftCode`=SKU); `Redeem` del proveedor para eGift | Dapper |

Los originales Pending se saltan todo esto (`mOrderID = MainID`, 2440-2441).

### 7.4 Los pagos y el pedido original (2684-2854), post-procesamiento (2856-2947)

| # | archivo:línea | tabla | escritura | condición |
|---|---|---|---|---|
| 12 | 2688-2700 | Payments (pedido de reembolso) | INSERT `BankID, CloseUserID=UserID=Global.UserID, OrderID=RefID=mOrderID, TerminalID, Amount=-_Ref, CloseDate=GETDATE(), Status=Refunded=8, Type = btnRefund ? eGift=7 : Certificate=12` OUTPUT PayID | `_FromItem` (reembolso sin recibo) |
| 13 | 2706-2733 | Orders (original) | `UPDATE SET [Status]=@Status, … WHERE [OrderID]`: Refund → Pending ? (`_Ref==_Remain` ? Open=0 : Pending=6) : Completed=1; Void → Voided=5; Pending: `Paid=Paid-_Ref`, si no `RefundAmt=ISNULL(RefundAmt,0)+_Ref`; Void añade `VoidDate=GETDATE()`. `ReasonID/Note/CloseDate` quedan sin tocar | no `_FromItem` (raw; los parámetros Output `@CustomerID/@FPID` nunca se asignan) |
| 14 | 2749-2753 | FloorPlan | `Status=6 WHERE FPID=@FPID` | **código muerto** (`@FPID` NULL) |
| 15 | 2768-2770 | Payments (original) | `RefundAmt=ISNULL(RefundAmt,0)+@RefAmt` (Status sin cambios) | por cada fila de `mPayRows` con Status Refunded=8 en memoria |
| 16 | 2774-2776 | Payments (pedido de reembolso) | INSERT `BankID; CCTxID = la nueva tx Return (tarjetas) / el CCTxID original (no tarjeta) / NULL (efectivo); CloseUserID=UserID=Global.UserID; OrderID=RefID=mOrderID; TerminalID; Amount=-RefAmt; CloseDate=GETDATE(); DeliveryFee=-Round(DeliveryFee×RefAmt/OriginalAmt,2) o 0; Status=8; TDUsed=RefTD, TFUsed=RefTF (en negativo), Surcharge=RefSurcharge (NULL para Type>=14); Tip = NULL si mTip==0, si no -min(RefAmt, mTip restante) en FIFO (2790-2805); Type=Type de la fila` OUTPUT PayID | igual que 15, original no Pending |
| 17 | 2817-2819 | Payments (original) | `RefundAmt=ISNULL(RefundAmt,0)+@RefAmt, Status=Voided=9` | por cada fila con Status Voided=9 en memoria (modo Void, o un pago Auth=6 en modo Refund) |
| 18 | 2823-2832 | CCTxs (original) | `RAmt=@RefAmt, Status=3` (+`Number=NULL, OffLineStatus=4` para Offline) | igual que 17 con un CCTxID |
| 19 | 2846-2850 | Terminals | `OffDate=NULL, OffSent=NULL`; `NoOffline(false)` | siempre que 17/18 no devuelvan ninguna fila (toda fila anulada que no sea offline) |
| G1 | csPayment.cs:94-118 vía 2894 | CCTxs; Payments | fila CCTxs de tarjeta de regalo (`AType=GiftCard, CardHolder='eGift', TxType=Return=5, Status=1, AuthCode=gift Reference`), y luego `Payments SET CCTxID, Reference WHERE PayID=mPayID` | `mGift<>0`, se pidió una tarjeta destino y `Activate` tuvo éxito |
| G2 | GiftCardManager.cs:129-142 vía 2895 | GiftCards | `Balance += mGift` en la tarjeta destino | igual que G1, la fila local existe |
| G3 | GiftCardManager.cs:370-402 vía 2906-2913 | GiftCards | INSERT del certificado `Amt=Balance=mCert, FromRefund=1, Used=0, CustomerID=Lib.ToInt(_CustomerID) (0 si no hay), OrderID=mOrderID` | `mCert>0` |
| — | 2918-2929 | (CRM) | `AccountReceivableManager.CreateTransaction` RETURN, `ParentRefId=MainID` | OnAccount reembolsado (solo en la nube) |
| 28 | 222-226 vía 3033 | Orders (original) | `InUseID=NULL` | fin del manejador sin Re-create |

Cajón de dinero cuando `OpenAlwaysCD` o `mCash<>0` (2934), CFD "Returned amount", `Print.Refund(mOrderID)` en un hilo (el pedido de reembolso en Refund, el original en Void; nada en Pending), impresión del certificado.

### 7.5 Re-create tras Void (2955-3016)

`INSERT INTO [Orders] … SELECT` de 21 columnas de cabecera desde MainID (incl. `InUseID`=este terminal, `Note, Tax, Tip, Total, DeliveryFee`) con `StaffID=Global.UserID, TerminalID=Global.TerminalID, Status=Open=0`, `Paid/RefID/ReasonID/FPID/CloseDate/Surcharge/RefundAmt/VoidDate` a NULL, `OUTPUT INSERTED.[OrderID]` (2961-2972, wrapper) → después un único lote raw (3014): `OrdersLog (nuevo, Global.UserID, Create=0)` (2975, `@Type=OrderLogType.Create` 3013); copia de `OrdersTax` con `CDate=GETDATE()` (2978); copia en `Payments` **solo de las filas ClubDiscount=15** con `Status=Open=0` (2983); `OrdersDelivery (nuevo, Global.UserID, New=0)` para los pedidos Delivery (2989); copia de `OrdersTips` con `StaffID=IIF(StaffID=_StaffID, Global.UserID, StaffID)`, `Type/PayID` a NULL (2994); copia de 67 columnas de `OrdersDetails` `WHERE ISNULL(Deleted,0)=0`, `Qty` en positivo, `RefQty` a NULL (2999); `UPDATE [Items] SET [QtyOnHand]-=Qty, [QtySold]+=Qty` a partir de las líneas del pedido nuevo `WHERE StockType<>2` (3004 — vuelve a descontar lo que la anulación había restituido). Después, `Global.Register.SearchOrder(newID)` + `Close()` (3029-3030): el `_LoadOrder` de Register se salta su propio bloqueo (`InUseID` ya está asignado) y el **original queda bloqueado**.

### 7.6 Reembolso abierto (`btnOpenClick` 1603-1873, `Refund.RefOpenEnded`, sin control de seguridad)

Importe (`frmMessageValue`, `MaxValue=RefOpenEndedAmount`), forma de pago (`frmPayType`: Cash/Credit/Check/Debit/Certificate/eGift según configuración), confirmación, motivo opcional (`frmNotes` Type 14; si no, `ReasonID=39`). Credit/Debit: `CardData{OrderID=0, PayID=DBNull, TxType=Return=5}`; Credit offline → `Offline=11`, `Offline(true)`, tarjeta manual, AuthCode falso, `SaveTrx` (1705) + `UPDATE [CCTxs] SET [Number]; UPDATE [Terminals] SET [OffDate]=GETDATE(),[OffSent]=NULL WHERE [OffDate] IS NULL` (1706-1719); Debit/EBT offline sigue adelante con un aviso y **escribe las filas igualmente** (1686-1687, bug); online → `Process` (1736; si falla → repliegue a certificado o cancelación). Cash abre el cajón de dinero cuando `OpenAlwaysCD`. Certificate → `mCert=importe`. eGift → `Activate` del proveedor (1794); `IssueCertificate` redirige a Certificate. Luego: `INSERT INTO [Orders] (CustomerID=NULL, ReasonID, StaffID=Global.UserID, TaxID, TerminalID, BaseAmount=-@Amount, CloseDate=GETDATE(), Note, OrderType=0, Paid=-@Amount, Status=OpenRefund=10, Tax=0, TDUsed=0, TFUsed=0, Total=-@Amount) OUTPUT INSERTED.[OrderID]` → `mRefID` (1807-1835, wrapper) y un único lote vía `ExecNonQuery` (1839-1854): `INSERT [Payments] (BankID, CCTxID, CloseUserID=UserID=Global.UserID, OrderID=@RefID, TerminalID, Amount=-@Amount, CloseDate=GETDATE(), Status=OpenRefunded=13, Type)`; `OrdersLog (RefID, UserID, 0)`, `(RefID, UserID, 1)`, `(RefID, MgrID, 2)`; `INSERT [OrdersDetails] (CatID=1, ItemID=Refund=1, OrderID, SubCatID=1, Description='Open Ended Refund', Price=PriceBase=PriceDisplay=PriceOriginal=PriceSell=+@Amount, Qty=-1, RefQty=1)`. Después, `Print.Refund(mRefID)` (en modo email puede hacer `INSERT [EmailsSent]`, Lib.cs:3233, en un hilo), `CreateGiftCertificate` + `Print.GiftCert` cuando `mCert>0` (1859-1869, `CustomerID=NULL, FromRefund=1`), "Refund completed.". Sin `OrdersTax`/`OrdersTips`, y no se toca ningún pedido existente.

### 7.7 Resumen de filas para las cuatro formas de reembolso

| Forma | Fila nueva en `Orders` | `Orders` original | `Payments` originales | `Payments` nuevos | `OrdersDetails` | Stock |
|---|---|---|---|---|---|---|
| Reembolso total (original Completed) | `Status=4, RefID=orig, Total=Paid=-Paid, CloseDate` | `Status=1` reescrito, `RefundAmt=Paid` | `RefundAmt=Amount`, se conserva Status 1 (las filas Auth → 9) | uno por forma de pago, `Status=8, Amount=-RefAmt, OrderID=RefID=nuevo`, las tarjetas enlazadas a un CCTxs Return | líneas originales `RefQty=Qty`; espejos negativos en el pedido de reembolso | `+QtyToRefund` (StockType<>2) |
| Reembolso parcial | igual, importes = la parte reembolsada | `RefundAmt += _Ref` | `RefundAmt += RefAmt` | `Amount=-RefAmt` | `RefQty += n`; espejo `Qty=-n` | `+n` |
| Reembolso de un original Pending | ninguna (`mOrderID=MainID`) | `Paid -= _Ref`, `Status` 0 si se revierte por completo, si no 6 | `RefundAmt += RefAmt` | ninguno | sin tocar | sin tocar |
| Void | ninguna | `Status=5, RefundAmt=Paid, VoidDate` | `Status=9, RefundAmt=Amount` (+ lote VoidAuth para las tarjetas) | ninguno | sin tocar | `+Qty` en cada línea |
| Abierto | `Status=10, Paid=Total=-Amount` | — | — | `Status=13, Amount=-Amount` | una línea `ItemID=1` con `Qty=-1` | ninguno |

## 8. Propinas

Las propinas viven en tres sitios que se escriben de forma independiente: `Orders.Tip` (el total a nivel de pedido, reescrito a `_TipAmt` al cerrar), `Payments.Tip` (la parte asignada a una forma de pago) y las filas de `OrdersTips` (una por evento de propina, tipada por `TipType`). No tienen por qué coincidir: `Orders.Tip` al cerrar es el `_TipAmt` completo y puede superar a `SUM(Payments.Tip)` (18652); `spTipAdjust` reemplaza `Orders.Tip` mientras inserta todavía otra fila de `OrdersTips`.

| Ruta | Disparador | `OrdersTips` | `Payments` | `Orders` | Dónde |
|---|---|---|---|---|---|
| Propina en Register (Add Tip / propina de grupo / porcentaje) | `btnAddTipClick` → `frmTips` → `btnSaveClick`; `btnPercentClick` | INSERT `(OrderID, StaffID, Amount, Type ∈ {Group=2, Amount=3, Percent=4}, Value)` OUTPUT TipID, `PayID` NULL; UPDATE por TipID; DELETE por TipID para las filas `Deleted` | — | `Tip=_TipAmt`, `Total` incluye `Round(_TipAmt,2)` (12540) en cada guardado | 2702-2737; 16243-16265 |
| Parte de propina por forma de pago al pagar | batch A | — | `Tip` = parte prorrateada de `(_TipAmt-_TipPaid)` o NULL; `FeeFromTips=Tip×TenderFee.Amount` para Credit/Debit con `DeductFeeFromTips` | `Tip=_TipAmt` al cerrar | 17994-18017, 18008; 18652 |
| Propina y firma en CFD (multitouch, antes de leer la tarjeta) | `Global.CFD.TipAndSignature` aceptado con propina ≠ 0 | INSERT `Type=CFD=7`, `PayID`=PayID de la fila, OUTPUT TipID → `mValues.Object4` | — | `Total += tip` (siempre en la base de datos; en memoria solo cuando no hay pagos divididos) | 16794-16798 |
| Propina en el terminal (PIN-pad), sin multitouch, `Account.TipReq` | tras una aprobación de tarjeta que no es de tab con `CardData.Tip<>0` | INSERT `Type=Terminal=5`, `PayID` | fila `Amount += tip` (en memoria, luego batch A) | `Total += tip`; `CCTxs.Tip=NULL` | 17546-17594 |
| Tab Complete / Complete Auth / tarjeta de crédito en reparto (`csPayment.Complete(pID, pTip)`) | 16915; `_OnComplete` 9324; frmDelManager.cs:659 | INSERT `(OrderID, PayID, StaffID=Orders.StaffID, Amount=tip, Type = Global.TouchScreen ? CFD=7 : CC=1, Value=tip)` cuando no hay fila para ese PayID (547), y si la hay UPDATE `Amount, Type, Value` (553) — solo con propina ≠ 0; la rama `TipType.Gift=6` de esa expresión (560) es inalcanzable porque la ruta de tarjeta de regalo nunca asigna `mCardData.AccountType` (solo lo hace la rama de tarjeta, 432/438), así que las finalizaciones de aprobación eGift también reciben 1/7 | `pTip==null` (o un Int32 -1 boxeado): `Amount += tip, Status=1, CloseUserID=Orders.StaffID` (495-500); **además** `Amount += tip` otra vez cuando `Global.TouchScreen` (565-569); `FeeFromTips` (575-579) | `pTip==null`: `Paid += tip, Tip += tip, Total += tip` (502-506); recargo (527) | csPayment.cs:293-588 |
| Propina en CFD y después rechazo de la tarjeta | `WithError` | `OrderID=-OrderID` en la fila de propina de CFD (19040) | fila con borrado lógico | `Total` revertido **solo para eGift** (19028) | 19012-19040 |
| Ajuste de propina tras el cierre (`frmReView`, Register → Complete → Review) | propina modificada, `CCTxs.Partial=0`; Credit → Adjustment del procesador, eGift → `CompleteAuth` | `CCTxs.Tip` anterior NULL → INSERT `Type=CC=1, StaffID=Orders.StaffID`; propina nueva 0 → DELETE de todas las filas de ese PayID; en caso contrario UPDATE `Amount/Value += Δ` | `Amount += Δ` (`Payments.Tip` nunca se toca) | `Paid += Δ, Tip=ISNULL(Tip,0)+Δ, Total += Δ`; `CCTxs.TransmitID, TxType=Adjustment=4` (151), `CCTxs.Tip = new or NULL` OUTPUT DELETED.Tip | frmReView.cs:151-190 |
| Cierre en efectivo desde el gestor de reparto | Complete con `frmComplete.Tip` (precargado desde `Orders.Tip`) | INSERT `(OrderID, PayID=new, StaffID=driver, Amount, Type=Amount=3, Value)` cuando la propina ≠ 0 | fila nueva `Tip=IIF(@Tip=0,NULL,@Tip)`, `Amount=(Total-Paid)+Tip` | `Paid=Total+Tip, Tip=@Tip, Total += Tip` (la propina existente se cuenta dos veces — pregunta abierta) | frmDelManager.cs:606-654 |
| Cierre con tarjeta de crédito desde el gestor de reparto | `Complete(PayID, tip)` (decimal → se saltan las líneas 495-509), luego batch H | igual que el `Complete` de arriba, `StaffID` = el repartidor que `SetDeliveryStatus` acaba de escribir | `Amount += Tip, CloseUserID=Global.UserID, Status=1` (batch H; con TouchScreen la propina se suma dos veces por la 565) | `Tip += Tip, Paid += Tip, Total += Tip` (batch H) | frmDelManager.cs:663-681 |
| Dispositivo de pago en mesa (`spApplyPayment`) | servicio externo Clover Flex | INSERT `(OrderID, PayID=new, StaffID=Orders.StaffID, Amount=@tip, Type=Terminal=5, Value=@tip)` — hay fila incluso con propina 0 | INSERT `Type=3 (always), Status=1, Tip=@tip, MachName='Clover Flex'` | `Paid += total, Total += tip, Tip += tip, PaidAtTable=1, Status/CloseDate` | db-procs.txt:58-72 (transacción explícita) |
| Ajuste del pago en mesa (`spTipAdjust`) | externo | INSERT de otra fila `Type=5` por cada llamada | `Amount=@total, Tip=@tip` (reemplazados) | `Paid += tip, Tip=@tip (reemplazado), Total += tip` | db-procs.txt:297-306 (transacción) |
| Refund | propinas seleccionadas con `_RefTip<>0` | `RefAmount=Amount` en la fila original; espejo `Amount=-Amount` en el pedido de reembolso | fila de reembolso `Tip=-min(RefAmt, remaining)` en FIFO; Return de tarjeta `CCTxs.Tip=-tip` | pedido de reembolso `Tip=-_RefTip` | frmRefund.cs:2530-2548, 2790-2805, 2360-2367 |
| Anulación de un solo pago (`VoidAuth`, `pVoidOrder=false`) | Void en frmComplete/frmReView | DELETE `WHERE PayID` | `CCTxs.Tip=0` | `Tip -= CCTxs.Tip, Total -= CCTxs.Tip` | csPayment.cs:1108, 1138-1142 |
| Liquidación offline rechazada | frmOfflineProcess | DELETE `WHERE OrderID AND PayID AND Type=CC=1` | — | `Tip -= CCTxs.Tip, Total -= same` | frmOfflineProcess.cs:200-204 |
| Anulaciones del pedido completo | `OrderManager.VoidOrder`, Void en frmRefund, Void en la lista de recuperación | sin tocar | — | sin tocar | 6.x |
| Split | `btnSplitClick`, tras la confirmación "tips will be removed" | se borran las propinas que no son de grupo; se reescribe el `Amount` de la propina de grupo del original y se crea una fila `Type=2` nueva por cada hijo con `StaffID`=el del original | se reescribe el `Amount` de la fila del tab | por cada sub-pedido `Tip=_TipAmt` | 19644, 19784-19795 |
| Reassign / Take / cambio de repartidor | — | `StaffID` reescrito en **todas** las filas de propina del pedido (19234/19246); `OrdersTips.StaffID=new WHERE StaffID=old` cuando `Orders.Tip<>0` (Lib.cs:6971) | — | `StaffID` | 19234-19259; Lib.cs:6947-6975 |

Límites de `FraudControl.TipSecurity`: una propina `Type=Amount=3` que supere `(_SubTotal − QtyDel×PriceSell)×TipSecurity` tras una reducción de cantidad se marca como `Deleted` en memoria (12753-12767); el diálogo de `Complete` y `frmReView` rechazan las propinas que pasan del límite (csPayment.cs:353-374; frmReView.cs:100-108).

## 9. Otras mutaciones

### 9.1 Pay In / Pay Out (`btnPayInOutClick` 16124-16178)

Solo se habilita con el carrito vacío. `frmPayInOut` necesita una fila de motivo en `CustomFields` (`Type` 16 PayIn / 17 PayOut, Active=1), una nota y un importe > 0; `Amount = ToDec(txtAmount) × (In ? 1 : -1)`. Ida y vuelta 1 (16135-16146, ExecuteScalar raw): `INSERT INTO [Orders] (StaffID=Global.UserID, TaxID=Settings.Tax.TaxID, TerminalID=Global.TerminalID, CloseDate=GETDATE(), OrderType=PayIn=6/PayOut=7, Status=Completed=1, Tax=0, Total=0, RefID=CustomFields.CustomID or NULL) OUTPUT INSERTED.[OrderID]` — `Paid`, `CustomerID`, `FPID` NULL. Ida y vuelta 2 (16148-16171, raw, un solo lote; se reutilizan los parámetros del comando 1): `INSERT [OrdersLog] (OrderID, Global.UserID, Create=0)`, `(…, Close=1)`; `INSERT [Payments] (BankID=LocalSetting.BankID or NULL, OrderID, RefID=CustomID or NULL, TerminalID, UserID, Amount=signed, CloseDate=GETDATE(), Reference=note, Status=Completed=1, Type=PayIn=8/PayOut=9)`; y, solo cuando `Global.LocalSetting.BankID != null` (16155), `INSERT [BankMod] (BankID, UserID=Global.UserID, MngrID=Global.MgrID, Cash=signed amount, Date=GETDATE(), Notes=note)`. Luego `Lib.OpenCashDrawer()`, recibo y `Clear()` (sin efecto en la base de datos porque `MainID` es DBNull). Sin `OrdersDetails` y sin control de seguridad. Si la ida y vuelta 2 lanza, queda un pedido Pay In/Out en Completed sin ningún pago.

### 9.2 Split por artículo (`btnSplitClick` 19547-19853)

Confirmaciones: se eliminarán las propinas que no sean de grupo (19554), puede que se eliminen las promociones (19566); `frmSplitItem` asigna `Split` 1..5 a las líneas movidas y crea filas compartidas (`ItemID=ShareItem=9`/`ShareService=11`, `Shared=1`, ODID NULL). Secuencia: `SaveOrder(sender)` (19597 — un pedido sin guardar se INSERTa; las filas compartidas reciben su ODID en el **original**); `DELETE FROM [OrdersTax] WHERE [OrderID]=MainID` [+ `DELETE [OrdersFPSplit]`] (19621-19633); `DELETE FROM [OrdersTips] WHERE [TipID]` por cada propina que no sea de grupo (19643-19647). Seis pasadas (19655-19800): la pasada 0 = `[Split] IS NULL AND [Deleted]=0` (se queda en el original), las pasadas 1..5 = los grupos movidos; cada pasada recarga `_OrderDetails` con su subconjunto y vuelve a ejecutar `_TotalUpdate`. Pasadas hijas: `INSERT INTO [Orders] (CustomerID, CustomID, FPID, OtherID, RefID, StaffID, TaxID, TerminalID, Label, LegalAge, LegalAgeMethod, LicenseName, Loyalty, Note, OrderType, Printed, Status, SplitPayments) SELECT … FROM [Orders] WHERE [OrderID]=MainID; SELECT SCOPE_IDENTITY()` (19615-19619, ejecutado en 19676, wrapper) — `Paid, InUseID, Exchange, CustExempt, Surcharge, TDUsed, TFUsed` NULL, `Status` copiado (normalmente 0); luego `INSERT [OrdersLog] (@ID=Global.UserID, @OrderID=child, @Type=OrderLogType.Create=0)` (19679-19694) [+ copia de `OrdersDelivery` para los tipos Delivery/DeliveryService/PickUp] (lote raw 19695). Pasada 0 con un Tab Auth: `UPDATE [Payments] SET [Amount]=_Total WHERE [PayID]=_TabAuthID` (19700-19706, sin llamada al procesador). En todas las pasadas: `INSERT [OrdersTax] (OrderID=sub-order, Qty, QtyExempt, Type=j, CDate=DateTime.Now)` por cada ranura aplicable (19710-19721); `UPDATE [OrdersDetails] SET [OrderID]=sub-order, [PriceSell], [Printed], [PromoUnit], [PromoLink], [PromoQty] WHERE [ODID]` por cada línea — **el movimiento**, con `CDate` sin tocar (19730-19741); [`INSERT [OrdersFPSplit] (FPID=_CurrentObj.ID, OrderID=sub-order, Status=Ordered=3 or TabAuth=7 for pass 0 with a tab)` +] `UPDATE [Orders] SET [BaseAmount],[Credit],[DeliveryFee],[ItemTotal],[Loyalty],[OtherAmount],[Tax],[Tip]=_TipAmt,[Total] [,Printed=1] OUTPUT INSERTED.[StaffID] WHERE [OrderID]=sub-order` (19743-19779; `@Exchange` se enlaza pero no aparece en el SET — el `Exchange` del original queda obsoleto); propina de grupo: `UPDATE [OrdersTips] SET [Amount]=_TipAmt WHERE [OrderID]=MainID` (pasada 0) / `INSERT [OrdersTips] (OrderID=child, StaffID=OUTPUT StaffID, Amount, Type=Group=2, Value)` (19781-19795). Luego `OrderDetailsAsync` por cada subpedido cuando hay print-orders/RemoteOnSave (csPrint.cs:1922 `Printed/Fire`), en el plano de salón `_CurrentObj.Status=Split=10; DBUpdate()` (19820-19821: `OrderID=MainID, InUse=0, Status=10, LastAct`), el selector `frmFPOrder` → `_LoadOrder` o `Clear()`; sin plano de salón → `Clear()` + `btnGetOrderClick` (19847-19848).

### 9.3 Exchange, eliminar línea, Clear All, exención de impuestos, verificación de edad (en memoria hasta el siguiente `SaveOrder`)

Exchange (`btnExchangeClick` 13807-13932): se inserta una fila `ItemID=NotPrinted=19` "** Exchange Item **" encima de la línea y la línea pasa a `Qty=-q` (13858-13887); `_TotalUpdate` suma las líneas de exchange en `_Exchange` → `Orders.Exchange` (12414-12415 → 2030/2104); la forma de pago negativa se persiste con `Payments.Status=Refunded=8` y no toca `Orders.Paid` (17960). Deshacer elimina la fila de la etiqueta sin encolar su ODID (13874-13877, pregunta abierta). Eliminar línea (`btnDeleteClick` 13482-13645): marca `Deleted` y `DelUserID`, y encola `csDelRow` (13542-13545) — SaveOrder lo persiste como el borrado lógico + la reposición de stock (3.8); al eliminar la última línea se pone `Modified=false` y se omite `Clear()` (13629-13635), así que los borrados encolados **no** se persisten hasta que algo más guarde y el bloqueo de uso se mantiene (pregunta abierta). Clear All (`btnClearClick` 13389): "Save Order" → `btnSaveClick(_CmdSave)`; "Clear All" → fila en `Log` con `NewValue='Clear All'` (AuditManager, 13416) y después `Clear()`. La exención de impuestos alterna `OrdersDetails.TaxExempt` (19866-19922, se persiste en 2430/2462; alimenta `OrdersTax.QtyExempt`); la verificación de edad fija `LegalAge/LegalAgeMethod/LicenseName` (13205-13213 → 1975-1977).

### 9.4 Join y Move (Lib.JoinOrder Lib.cs:4902-5116; plano de salón 14706-15281; frmGetOrders Join 231-312)

**Join** (botón Join de la cuadrícula, o Join de dos mesas sin dividir en el plano de salón): por cada origen, un `UPDATE [Payments] SET [Status]=9 WHERE [OrderID]=source AND [Type]=15` opcional (4944, raw) y después un solo lote mediante `ExecScalar` (4953-5023): los tipos de `OrdersTax` que le faltan al destino se reasignan a él, los tipos comunes se suman en el destino y las filas de impuestos del origen se dejan en blanco (`Qty/QtyExempt/Type=NULL`, no se borran); todas las `OrdersDetails` (incluidas las Deleted) → `OrderID=target, LinePos += MAX(target)+1` (4982); `OrdersPromos`, `Payments` (con cualquier Status) y `OrdersTips` se reasignan (4987/4992/5009); en el destino `BaseAmount, ItemTotal, Tax, Total += source`, `Paid/Tip/TDUsed/TFUsed` se suman con ISNULL y `Status=IIF(T=S, T, Pending=6)` (4996-5000); en el origen `StaffID=Global.UserID, Status=JoinIn=2, Total=0, Tax=0, CloseDate=GETDATE(), RefID=target OUTPUT FPID` (5013). Plano de salón: se borran las filas de `OrdersFPSplit` (5031-5062) y la mesa pasa a Ordered/CleanUp solo mediante `UpdateFP` + difusión — **`FloorPlan.OrderID` sigue apuntando después al origen en JoinIn**. Con un `TabAuthID` en el destino: `UPDATE [Payments] SET [Amount]=Orders.Total-ISNULL(Paid,0) … JOIN Orders ON PayID=TabAuthID` **sin filtro por OrderID** (5082 — afecta a toda la base de datos); se borran las propinas Group sobrantes del destino (5090-5111). frmGetOrders devuelve `DialogResult.Yes` de forma incondicional (312), así que el register ejecuta a continuación `_CheckForSave`, `_LoadOrder(target)` (bloqueo de uso 8922), un `SaveOrder` completo cuando se disparó `OnJoinOrder` (14053) y `UPDATE [Orders] SET [Tax]=_Tax,[Total]=_Total` (14060) — los totales que sumó el lote son transitorios. Los orígenes de tipo Delivery se descartan en silencio (frmGetOrders.cs:252-256). El Join desde el plano de salón con un origen en Tab Auth ejecuta primero `VoidAuth(TabAuthID)` (14918: CCTxs 3, Payments 9, `Orders.TabAuthID=NULL`); si alguna de las dos mesas está en Split, los pedidos solo se **reasignan** — `UPDATE [Orders] SET [FPID]=@FPID …; INSERT/UPDATE [OrdersFPSplit]` (14928/14942/14960) después de poner el destino en `Split=10` (14972).

**Move** (plano de salón, el destino debe estar `Open=1`): `UPDATE [Orders] SET [FPID]=@FPID` para el pedido de origen (14867) o, si el origen está en Split, para todas sus cuentas + `OrdersFPSplit.FPID` (14836); mesa de destino `OrderID=source order, Status=source Status, InUse=0, LastAct` (14880); mesa de origen `OrderID=NULL, Status=6` (14886). `Orders.StaffID/Status/InUseID` no cambian. Ojo con la apropiación silenciosa al pulsar cualquier mesa en uso: `UPDATE [Orders] SET [StaffID]=Global.UserID` **antes** del diálogo (14802/14808), que "None" no revierte.

### 9.5 Reassign / Take (`btnReassignClick` 19165-19276)

Es inmediato, no se difiere al Save. Un solo lote: `UPDATE [Orders] SET [StaffID]=@StaffID …; UPDATE [OrdersTips] SET [StaffID]=@StaffID …` — `WHERE [OrderID]=MainID` (19246) o `JOIN OrdersFPSplit WHERE FPID` para cada cuenta de una mesa en Split (19234); **todas** las filas de propina, no solo las de tipo Group. Reassign elige de `Staff WHERE Active=1 AND SoftAccess=1`; Take usa `Global.UserID`. El campo `StaffID` del formulario se asigna antes de la ejecución (19255), de modo que un `SaveOrder` posterior de un pedido **nuevo** inserta ese StaffID y el `ID` de su fila Create, que puede diferir de `Payments.UserID=Global.UserID` (pregunta abierta 4 de db-plumbing — es intencionado después de un Reassign). El Take desde el plano de salón continúa hasta `_LoadOrder` (15205); Reassign libera la mesa (19269).

### 9.6 Recuperación y el bloqueo de uso (`_LoadOrder` 8520-8953, `Clear` 732-1059)

`_LoadOrder` llama primero a `Clear()` (8522: `UPDATE [Orders] SET [InUseID]=NULL` para el pedido cargado antes, 739; FloorPlan `InUse=0` para `_CurrentObj`/`_SelectedObj`, 1001/1012). Lee la fila de Orders y aborta si hay un bloqueo ajeno (`InUseID` no NULL y distinto de este terminal; bloqueo duro en 8557 salvo que `Global.SystemAdmin` lo confirme, 8546-8553) o si `Status=Completed=1` (8565 — la comprobación del bloqueo va primero, así que un pedido en Completed con un bloqueo obsoleto informa "Opened on terminal X"). Con `Orders.FPID` convierte la mesa en `_CurrentObj` con `InUse=true` y escribe `FloorPlan SET OrderID=<object's existing OrderID>, InUse=1, Status=<unchanged>` (8674). Se cargan detalles, propinas, impuestos, reparto, cliente y pagos (`Status<>9`); los totales se recalculan en memoria. La **última** sentencia es `UPDATE [Orders] SET [InUseID]=@InUseID (Global.TerminalID) WHERE [OrderID]` (8922-8925), **solo si `InUseID` era NULL** en el momento de la lectura. Liberación: `Clear()` (Save 19407, guardado automático 6399/6413, Fire Now 13973, salida de un tab ajeno 16543, finalización 18950-18971, Tab Auth 17537, anulación desde la cuadrícula 14042, botón Refund 19283, split 19838/19847, Clear All 13420) y `OnFormClosing` (2969) — `InUseID=NULL` sea cual sea el Status. Un terminal que muere deja un bloqueo obsoleto; `frmRefund` usa la misma columna con su propio set/clear (7.1). frmGetOrders lista `Orders.Status IN (0,6)` (frmGetOrders.cs:114), solo los del propio empleado cuando `OpenAllOrder > AllowLog` (119). Print Order (`_PrintOrder` 9920-9999): `UPDATE [Orders] SET [Printed]=1 OUTPUT INSERTED.[FPID],INSERTED.[TabAuthID]` (9933); mesa sin tab → `FloorPlan Status=Check=4` (9959 raw cuando la mesa está fuera de pantalla, con la guarda `Status<>10`; si no, `DBUpdate` 9971, y la primera impresión escribe `InUse=0, LastAct`); la impresión de la cuenta del cliente nunca toca `OrdersDetails.Printed`. Fire now desde el plano de salón: `UPDATE [OrdersDetails] SET [Fire]=1 WHERE [ODID]` por cada fila sin imprimir (14001) y después impresión remota (`Printed=Qty, Fire=0`, csPrint.cs:1922). Cambio de tipo de pedido en un pedido de mesa ya guardado: lote `UPDATE [Orders] SET [FPID]=NULL; UPDATE [FloorPlan] SET [OrderID]=NULL,[InUse]=0,[LastAct]=NULL,[Status]=6 WHERE [OrderID]` (20055); el nuevo `OrderType` en sí solo lo persiste `SaveOrder`. Reset All (19301-19345): las mesas en CleanUp → `OrderID=NULL, InUse=0, LastAct=@Now (client clock), Status=Open=1` (19309-19325); las demás mesas en uso → `InUse=0` (19330); Orders no se toca. Diálogo Available/Occupied del plano de salón: `DELETE [OrdersFPSplit] WHERE FPID` (15162/15179) y `FloorPlan OrderID=NULL, InUse=0, Status=1|2` (15190) — **la fila de Orders no se toca** (enlace huérfano; la siguiente recuperación la vuelve a enganchar vía `Orders.FPID`). El receptor de socket (`UpdateFP`, 2900-2962) escribe `FloorPlan Status=6, OrderID=NULL, InUse=0` cuando el OrderID de una trama ya no existe o procede del dispositivo LRS (2946-2955 → csObject.cs:472).

### 9.7 Gestor de reparto (`frmDelManager`, Lib.SetDeliveryStatus Lib.cs:6901-7057)

Cuadrícula: `Orders.InUseID NULL`, `OrderType IN (3,5,2)`, `OrdersDelivery.Status<>Undeliverable`, Open/Pending o no entregados antes de hoy; Payments se une con LEFT JOIN solo para `TerminalID=this AND Status=Auth=6` (399-401). **Complete**: preasignación del repartidor, `Delivery\frmComplete` (propina precargada desde `Orders.Tip`, deshabilitada para los pedidos online) y después `_HandleRow` → `SetDeliveryStatus(Delivered)`: batch J `UPDATE [Staff] SET [DeliveryStatus] …; UPDATE [Orders] SET [StaffID]=@new OUTPUT DELETED.[StaffID]` (6942-6952), `UPDATE [OrdersDelivery] SET [StaffID]` (6955), `OrdersDetails/OrdersTips StaffID=new WHERE StaffID=old` cuando cambió el repartidor y `Orders.Tip<>0` (6966-6975), se recalcula el estado del repartidor anterior (7004), `UPDATE [OrdersDelivery] SET [Delivered]=@Now,[Out]=ISNULL([Out],@Now),[Ready]=ISNULL([Ready],@Now),[Status]=3` (7016-7021), recibo de recogida y difusión de `SocketCode.Delivery=242` (7055). Después, **efectivo/3PDS** (sin pago Auth en este terminal): batch G (594-627): `INSERT [OrdersLog] (OrderID, ID=driver or NULL, Type=OrderLogType.Close=1)` (595, `@Type` se enlaza en 643); `OpenPromoDetails` → `OrdersPromoDetails`; `UPDATE [Orders] SET [BatchID]=NULL,[CloseDate]=GETDATE(),[Paid]=[Total]+@Tip,[Status]=1,[StaffID]=@UserID,[Tip]=@Tip,[Total]=[Total]+@Tip` (606); `INSERT [Payments] (BankID, OrderID, TerminalID, UserID=driver, Amount=(Total-Paid)+Tip, DeliveryFee=driver share, RefundAmt=0, Status=1, Tip=IIF(@Tip=0,NULL,@Tip), Type = 3PDSID NULL ? Cash=0 : DeliveryService=16, Value=Amount, CloseDate=GETDATE()) OUTPUT PayID` (616); `UPDATE [Customers] SET [PrevOrder]=[LastOrder],[LastOrder]=@OrderID` (625); con propina ≠ 0, `INSERT [OrdersTips] (…, Type=Amount=3)` (651-654). Sin cajón de dinero y sin llamada al recibo. **Crédito** (Auth=6 en este terminal): `_OnComplete` → `csPayment.Complete(PayID, tip)` (5.10) y después batch H (663-681). **Undeliverable** (697-726): primero `SetDeliveryStatus` (puede reasignar / promover a Delivered un PickUp ya pagado), luego `UPDATE [Orders] SET [Status]=Undeliverable=11; UPDATE [OrdersDelivery] SET [Status]=4` (706-717) y la difusión. Los botones de estado (Ready/Out) escriben marcas de tiempo en `OrdersDelivery` + `Status=1/2` (7030/7042) y `Staff.DeliveryStatus`.

### 9.8 Precio dual, pedidos online, cargos por tiempo, editor del plano de salón, actualización de frmMain

Las escrituras de precio dual solo se llaman desde `btnProcessClick` (5.4, 5.7): `UpdatePaymentRecordWithTFUsed` → `UPDATE [Payments] SET [TFUsed]=@TFUsed WHERE [PayID]` (csDualPriceManager.cs:278, sin efecto cuando vale 0 o PayID 0), `UpdateOrderTotal` → `UPDATE [Orders] SET [Total]` (301), y la reescritura de finalización de `Orders/OrdersDetails/OrdersTax/OpenPromoDetails/OrdersDelivery/Payments` (690-995). Pedidos online: un hilo que se ejecuta una vez por minuto aplica `UPDATE [Orders] SET [Printed]=1 WHERE [OrderID]` a las filas `OnlineOrder=1, Printed=0` cuyo `OOTime` ya venció (frmMain.cs:3300-3347) y las imprime en remoto. Cargos por tiempo: en `frmTime`, Start hace `INSERT [OrdersTimeCharge] (ODID, OrderID, StartTime=GETDATE())` (frmTime.cs:104) y Stop `UPDATE … SET [EndTime]=GETDATE()` (111); la finalización los borra (18623). OK del editor del plano de salón (frmSetFloorMap.cs:589-662): `CustomFields.Image` (593), mesas borradas → `DELETE [FloorPlan]; DELETE [OrdersFPSplit]; UPDATE [Orders] SET [FPID]=NULL WHERE [FPID]` (614-620, todo el histórico), mesas nuevas o actualizadas (645/651, `Status=Open=1` para las nuevas) y reescritura de `Config.GlobalXML` (Lib.cs:7487). Actualización de una sola vez `UPDATE [OrdersDetails] SET [PriceDisplay]` para los padres de agrupación (frmMain.cs:1361-1420).

### 9.9 Resultado del procesamiento offline (`frmOfflineProcess`, desde `CloseBatch` en una compilación sin DEBUG)

Filas: `Payments.Type=11 JOIN CCTxs` en este terminal, `Number NOT NULL`, `Status<>3` (60-71). Cada una pasa por `Default.ProcessAsync` (127-132). **Aprobada** → batch D: `UPDATE [CCTxs] SET [TID],[AuthCode],[CloseDate]=ISNULL([CloseDate],@Now),[OffLineStatus]=Approved=3,[PNRef],[Number]=NULL,[Status]=1,[TransmitID],[TxType]=(restaurant ? Capture=1 : Sale=2); UPDATE [Payments] SET [CloseUserID]=Global.UserID,[Status]=1,[Type]=Credit=3` (138-162). **Fallida/timeout** → `UPDATE [CCTxs] SET [OffLineStatus]=Failed=1` (se conserva `Number` para reintentar, 176). **Rechazada** → batch E: `UPDATE [CCTxs] SET [Number]=NULL,[OffLineStatus]=Declined=2; UPDATE [Payments] SET [Status]=Deleted=14 (Type stays 11); UPDATE [Orders] SET [CloseDate]=NULL,[Paid]=[Paid]-ABS(Amount),[Status]=IIF(old Paid=@Amount, Open=0, Pending=6),[Tip]-=CCTxs.Tip,[Total]-=same; DELETE [OrdersTips] WHERE [OrderID] AND [PayID] AND [Type]=CC=1` (192-214; `BatchID` sin tocar; en los Return offline negativos `Paid` se mueve en el sentido equivocado — pregunta abierta). Sin ningún fallo: `UPDATE [Terminals] SET [OffDate]=NULL,[OffSent]=NULL` (236-240). En **esta compilación** `CloseBatch` nunca llega a frmOfflineProcess (5.10).

### 9.10 Sesiones de cajón (bank), cierre de lote, registro de terminales, reinicio de transacciones

Bank Swap (13243-13381): lote de swap-out `UPDATE [Payments] SET [BankID]=NULL WHERE [Status] NOT IN (1,8,9) AND [BankID]=@BankID; UPDATE [Bank] SET [ShiftEnd]=GETDATE(),[Status]=SwapOut=2` (13337-13350) y después `LocalSetting.BankID=null`; lote de swap-in `UPDATE [Payments] SET [BankID]=@BankID WHERE [Status] NOT IN (1,8,9) AND [BankID] IS NULL` — **adopta los pagos abiertos de todos los terminales** — `; UPDATE [Bank] SET [TerminalID],[ShiftBegin]=GETDATE(),[Status]=SwapIn=1` (13358-13373), y después `LocalSetting.BankID=mBankID` y la reescritura de `Exatouch.cfg` (13374-13377). `frmBank` INSERTa `Bank (Status=Open=0)` (frmBank.cs:272-275); `frmBankClose` INSERTa una fila de `BankDetails` por cada forma de pago (`Amount` contado, `Expected`) y `UPDATE [Bank] SET [Delta],[Status]=Closed=3,[PayOutIn]=SUM(BankMod.Cash),[RefCash]` (frmBankClose.cs:143-164) sin limpiar `LocalSetting.BankID`. El cierre de lote en esta compilación: 5.10. Registro del terminal al arrancar: frmMain.cs:1927-1943. `Lib.ResetTrxs` (Lib.cs:6345-6444; DEMO a los 100 pedidos, `frmReserved.cs:946`) hace TRUNCATE de todas las tablas de pedidos/pagos/bank/fichaje/log y reinicia `FloorPlan`, `Staff`, `Terminals`, `Items` — las identidades vuelven a empezar en 1.

### 9.11 Escritores periféricos de filas del lado del pedido (listados, no trazados fila por fila)

Estos tocan filas de pedido, reparto, bank o artículos desde pantallas ajenas a los flujos anteriores. Una prueba que los recorra debe prever esas escrituras; las listas de columnas salen de un grep del texto SQL, no de un trazado completo.

| Dónde | Escritura | Cuándo |
|---|---|---|
| frmDelManager.cs:901, 917 (`btnResetStatusClick`) | `UPDATE [OrdersDelivery] SET StaffID=NULL, Delivered=NULL, Out=NULL, Ready=NULL, Status=@Status OUTPUT DELETED.StaffID`; `UPDATE [Staff] SET DeliveryStatus=Available` para el repartidor liberado | Reset Status en el gestor de reparto |
| frmDelManager.cs:986, 1030 (`btnStatusClick`, "Driver(s) removed.") | `UPDATE [OrdersDelivery] SET StaffID=NULL` (solo mientras `Status<=Ready`); `UPDATE [Staff] SET DeliveryStatus=@NewStatus` recalculado a partir de las filas restantes del repartidor | repartidor quitado de un pedido |
| Transactions\Delivery\frmMap.cs:100, 143 (`_Remove`) | el mismo par de sentencias desde el mapa de reparto | repartidor quitado en el mapa |
| Transactions\Delivery\frmReorder.cs:115 | `UPDATE [Customers] SET DeliveryInstruction` | el diálogo de repetición de pedido guarda las instrucciones |
| Transactions\Delivery\frmDeliveryAddress.cs:150/179/188 | DELETE / INSERT / UPDATE sobre `CustomersAddresses` | selector de dirección de reparto |
| frmRegister.cs:7998 | `UPDATE [Customers] SET [Distance]` | búsqueda de cliente para reparto propio con verificación de dirección |
| Transactions\SubForms\frmSendEmail.cs:138/147 | `INSERT INTO [Customers] (FirstName, LastName, Email, FirstStaffID)` o `UPDATE [Customers] SET FirstName, LastName, Email` | diálogo de recibo por correo para un cliente desconocido / conocido |
| Transactions\SubForms\frmInfo.cs:139/150/157 | `UPDATE [Items] SET Unavailable=GETDATE()` (o NULL para deshacer el 86); `UPDATE [FreshSheet] SET Qty=0` | ventana emergente de información del artículo, "86 this item" |
| Transactions\Bank\frmBank.cs:169/280/290/315 | `INSERT INTO [Bank] … SELECT` una fila por día (duplicado); `UPDATE [Bank]` (edición); `INSERT INTO [BankMod] (…, Cash=_Total-_Open, Notes=adjustment note)` al editar un bank SwapIn; `UPDATE [Bank] SET Active=0` ("Void bank?") — esta última cambia la precondición de "bank swapped in" que hay detrás de `Payments.BankID` | Bank Manager |
| Transactions\SubForms\frmTakePayment.cs:156 → AccountPaymentManager.cs:201-256 | `INSERT INTO AccountPayments (CustomerId, TenderType, PayType, CCTxId, AmountApplied, AmountPaid, AmountReceived, Surcharge, Note, Date, CreatedAt, TerminalId, TerminalName, StaffName, CreatedBy, TransactionId, Reference, Rate=0, Deleted=0); SELECT SCOPE_IDENTITY()` más una fila de `AccountPaymentDetails` por cada factura imputada (`[Order]`, OrderId, Name, Amount) — solo se hace commit tras el éxito de la subida al CRM, y en caso contrario se hace rollback; la fila `CCTxs` de la tarjeta (escrita por `SaveTrx` con `PayID=0`) sobrevive en ambos casos | "Take Payment" de A/R |
| frmRegister.cs:14842, 14934, 14948, 14951, 14964 | UPDATE/INSERT de `OrdersFPSplit` `(FPID, OrderID, Status)` cuando una mesa Split es el origen o el destino de Move/Join (Status = el estado previo al Split de la mesa destino) | Move / Join en el plano de salón con una mesa dividida |
| Lib.cs:5031-5062 | borrados en `OrdersFPSplit` según la combinación dividida/no dividida tras un join (siempre la fila origen; la fila destino cuando queda una en la misma mesa; la fila origen cuando los FPID difieren) | rama de plano de salón de `Lib.JoinOrder` |
| csDualPriceManager.cs:690-995 | listas SET completas de la reescritura de finalización: Orders `BaseAmount, ItemTotal, Tax, [DeliveryFee], Total`; OrdersDetails `DiscUnit, Price, PriceBase, PriceDisplay, PriceOriginal, PriceSell, PriceSinTax, PriceTax, PromoPrice, PromoUnit, PromoUnitDisc`; OrdersTax `Qty, QtyExempt`; OpenPromoDetails `Value`; OrdersDelivery `DeliveryFee`; Payments de cupón `Amount`; Payments de club `Amount, TFUsed` | finalización con precio dual (5.7) |
| Reports\csPrint.cs:500 | `UPDATE [Payments] SET [TerminalID]=Orders.TerminalID WHERE PayID AND TerminalID IS NULL` | impresión del recibo de una fila de pago sin terminal (normalmente sin efecto) |
| Settings\frmFieldManager.cs:467/479/491/503/511 | `UPDATE [OrdersDetails]` ×4 y `UPDATE [Orders] SET [CustomID]=NULL` al eliminar un campo personalizado o un tipo de pedido | Settings → Field Manager |
| Staff\frmPayout.cs:140 | `UPDATE [OrdersDetails] SET [PayOutID] WHERE [TxDetailID]` — ninguna de las dos columnas existe en el esquema real: código muerto/heredado que lanzaría una excepción si se alcanzara | payout de Staff |
| Items\frmPromo.cs:548 | `DELETE FROM [OrdersPromos] WHERE PromotionID AND OrderID IS NULL` | se desactiva el flag Serial de una promoción |

## 10. Máquinas de estados

### 10.1 `Orders.Status`

| origen | destino | disparador | ubicación |
|---|---|---|---|
| (ninguno) | Open=0 | primer `SaveOrder` de un carrito (Save, Pay, Tab Auth, origen de un Split, Fire Now, autoguardado) | 2030 (`@Status` 1858) |
| (ninguno) | Open=0 | hijo de un split (Status copiado del original) | 19615 |
| (ninguno) | Open=0 | pedido recreado tras un Void en frmRefund | frmRefund.cs:2961-2972 |
| (ninguno) | Completed=1 | Pay In / Pay Out | 16135 |
| (ninguno) | Refunded=4 | pedido de reembolso (`RefID`=el original) | frmRefund.cs:2448 |
| (ninguno) | OpenRefund=10 | reembolso abierto | frmRefund.cs:1808 |
| 0 o 6 | Pending=6 | cada forma de pago que no sea descuento al hacerle commit (también la última) | 17959-17979 |
| 6 (o 0) | Completed=1 (+`CloseDate`, `BatchID`, `Tip`, `Surcharge`) | saldo 0 y no parcial — hilo en segundo plano; también cuando los pagos siguen en Auth=6 | 18637-18651 |
| 0 o 6 | Completed=1 (+`CloseDate`, `Paid=Total+Tip`, `StaffID`) | cierre en efectivo/3PDS del gestor de reparto | frmDelManager.cs:606 |
| 0 o 6 (sin comprobar) | 1 si `Paid+amount >= old Total`, si no 6 (+`PaidAtTable=1`) | `spApplyPayment` de pago en mesa | db-procs.txt:70-72 |
| 0 o 6 | Pending=6 | `WithError` tras haberse completado otra forma de pago | 19058 |
| 0 | Voided=5 (+`CloseDate`, `ReasonID`, `Note`) | Void en la lista de recuperación | frmGetOrders.cs:429 |
| 1 | Voided=5 (+`RefundAmt=Paid`, `VoidDate`) | `OrderManager.VoidOrder` (activación de eGift cancelada al completar) | OrderManager.cs:167-174 |
| 1 (no pendiente, `RefundAmt=0`) | Voided=5 (+`RefundAmt=_Ref`, `VoidDate`) | Void en frmRefund | frmRefund.cs:2706-2725 |
| 1 | 1 (reescrito, `RefundAmt += _Ref`) | Refund en frmRefund (total o parcial) | frmRefund.cs:2706-2725 |
| 6 | 0 cuando `_Ref==_Remain`, si no 6 (`Paid -= _Ref`) | Refund en frmRefund de un pedido Pending | frmRefund.cs:2706-2725 |
| 1 o 6 | 0 cuando no queda ningún pago vivo, si no 6 (`Paid -= Amount`) | `VoidAuth` de un pago que no es de tab con `pVoidOrder=false` (Void en frmComplete / frmReView) | csPayment.cs:1137-1139 |
| 1 o 6 | 0 si `Paid` era igual al importe, si no 6 (`CloseDate=NULL`) | liquidación offline rechazada | frmOfflineProcess.cs:200 |
| 0 o 6 | JoinIn=2 (+`RefID`=el destino, `Total=0`, `Tax=0`, `CloseDate`, `StaffID=Global.UserID`) | origen del Join | Lib.cs:5013 |
| T | Pending=6 cuando el Status del origen y el del destino difieren | destino del Join | Lib.cs:4996-5000 |
| cualquiera | Undeliverable=11 | Undeliverable en el gestor de reparto | frmDelManager.cs:706 |
| cualquiera | sin cambios | volver a guardar (`Status` no figura en la lista del UPDATE SET); Reassign/Move/Print/tab auth | 2093; 19246; 14867; 9933; 17490 |
| — | Deleted=3, RefPending=7, VoidPending=8, RefProcessing=9 | **no se encontró ningún escritor** en el código trazado (RefPending=7 solo lo lee frmSearch) | — |

### 10.2 `Payments.Status`

| origen | destino | disparador | ubicación |
|---|---|---|---|
| (ninguno) | Open=0 (`RefundAmt=0`) | `SaveOrder` inserta todas las formas de pago preparadas antes de procesarlas | 2595 |
| (ninguno) | Completed=1 | Pay In/Out; fila sintética de cash-back; cierre en efectivo del reparto; `spApplyPayment` | 16152; 18352; frmDelManager.cs:616; db-procs.txt:63 |
| (ninguno) | Refunded=8 (`Amount<0`) | pago del pedido de reembolso | frmRefund.cs:2774 / 2688 |
| (ninguno) | OpenRefunded=13 | reembolso abierto | frmRefund.cs:1840 |
| (ninguno) | Open=0 | pedido recreado (solo filas ClubDiscount) | frmRefund.cs:2983 |
| 0 / RemoteError=5 / 11 | Completed=1; Auth=6 cuando `mIsAuth` (Credit en restaurante, eGift en restaurante con saldo); Refunded=8 en los intercambios | commit por forma de pago (batch A) | 17938-17967 |
| 0 | TabAuth=11 (`Value=AuthAmount`) | se inicia un tab auth | 17486-17514 |
| 11 | Completed=1 (`Amount+=tip`, `CloseUserID=Orders.StaffID`) | `Complete(pID, null)` sin multitouch; después el batch A reescribe Status=1 | csPayment.cs:495-500; 17967 |
| 6 | Completed=1 | ventana Complete Auth; Batch Close (en esta compilación: todas las filas sin lote); cierre de lote sin DEBUG (filas eGift); crédito de reparto batch H; aprobado offline (`Type` 11→3) | csPayment.cs:495, 167-174, 1252; frmDelManager.cs:669; frmOfflineProcess.cs:142 |
| 0 | Deleted=14 (`OrderID=-OrderID`) | `WithError` en las filas Credit/Debit/EBT/eGift/Offline/OnAccount/Certificate | 18996 |
| 0 | Deleted=14 | Void en la lista de recuperación | frmGetOrders.cs:425 |
| cualquiera (offline) | Deleted=14 (`Type` sigue en 11) | liquidación offline rechazada | frmOfflineProcess.cs:196 |
| 0 / 1 / 6 / 11 | Voided=9 (`CloseDate`; en las filas de tab `Amount=Value`) | `VoidAuth` (Void en frmComplete/frmReView, Join de un tab en el plano de salón, cancelación de una aprobación parcial, tab cerrado por otra forma de pago, filas de tarjeta de frmRefund) | csPayment.cs:1116-1118 |
| 1 / 6 | Voided=9 (`RefundAmt=Amount`, `CloseDate`) | `OrderManager.VoidOrder` | OrderManager.cs:152-156 |
| 1 / 6 / 9 | Voided=9 (`RefundAmt += Amount`) | Void en frmRefund, o un pago Auth dentro de un Refund | frmRefund.cs:2817 |
| 1 | 1 (`RefundAmt += RefAmt`) | Refund en frmRefund | frmRefund.cs:2768 |
| cualquiera | Voided=9 | filas ClubDiscount del origen del Join | Lib.cs:4944 |
| 0 (`Type`=Check=1) | Completed=1, `Type`→CheckCashBack=14 cuando el cheque supera el importe debido | sección de cash-back | 18322-18373 |
| 0 con PayID | Status sin cambios, `OrderID=-OrderID` | forma de pago eliminada (`_ResetPay`) y luego guardada | 2564 |
| — | RemoteQueue=2, RemoteProgress=3, RemoteError=5 | se leen en 16445 (las filas RemoteError se reprocesan); **ningún escritor** en el código trazado (dispositivo remoto/ScanToPay) | — |

### 10.3 `CCTxs.Status` / `TxType` / `OffLineStatus`

| origen | destino | disparador | ubicación |
|---|---|---|---|
| (ninguno) | `Status` Authorized=0 cuando `TxType`=Auth=0, si no Completed=1; `TxType` 0/2/3/5 | `SaveTrx` tras un Sale/Auth/Force/Return aprobado (tarjeta en register, eGift, Return de reembolso, Return abierto, tarjeta A/R, aprobación falsa offline) | csPayment.cs:82-104 |
| (ninguno) | `Status` 1 (o 6, que no es miembro), `TxType` 4, `BatchClose=GETDATE()` | `spApplyPayment` | db-procs.txt:58-59 |
| 0 o 1 | TabAuth=2 (`TxType` sigue en 0) | se inicia un tab auth | 17486 |
| 0 o 2 | Completed=1, `TxType`=Capture=1, `CloseDate`, `PNRef`/`TransmitID` sobrescritos, `Tip` | `csPayment.Complete` (Tab Complete, ventana Complete, crédito de reparto, cierre de lote sin DEBUG) | csPayment.cs:482-491 |
| 0 o 1 | Completed=1, `TxType`=1, `Tip=0`, `CloseDate` | aprobación parcial aceptada | 17432 |
| cualquiera | Voided=3, `TxType`=Void=6, `Tip=0` (+`Number=NULL`, `OffLineStatus`=Voided=4 en las offline) | `VoidAuth` | csPayment.cs:1108-1114 |
| cualquiera | Voided=3, `RAmt=Amount`, `Tip=0` (+reinicio de offline) | `OrderManager.VoidOrder`; filas de Void de frmRefund (`RAmt=RefAmt`) | OrderManager.cs:98-108; frmRefund.cs:2823-2832 |
| 1 / 0 / 4 | `TxType`=Adjustment=4, `TransmitID`, `Tip`=nueva o NULL | ajuste de propina en frmReView | frmReView.cs:151-171 |
| cualquiera con `BatchClose NULL AND Number NULL` | `BatchClose=@Now`; 0→1 con `CloseDate`; `TxType` 0→1 (incluidas las filas TabAuth) | Batch Close en esta compilación | csPayment.cs:176-179 |
| `OffLineStatus` NULL | Approved=3 (`Number=NULL`, `Status=1`, `TxType` 1/2) / Failed=1 / Declined=2 (`Number=NULL`) | liquidación offline | frmOfflineProcess.cs:138 / 176 / 192 |
| `Number` NULL | datos de tarjeta cifrados con RSA | venta/reembolso offline; canje de eGift | 17237; frmRefund.cs:2310, 1707; CardTransactionManager.cs:28 |

### 10.4 `FloorPlan.Status` (y `OrdersFPSplit`)

| origen | destino | disparador | ubicación |
|---|---|---|---|
| Open=1 / Busy=2 | Ordered=3, `OrderID=MainID`, `InUse=0`, `LastAct` | primer guardado de un pedido en la mesa (si pasa la comprobación de csObject) | 2076-2084 → csObject.cs:472 |
| Check=4 | Ordered=3 | menú emergente Ordered/FullDebit en una mesa sin split y sin tab | 15132 → 15208 |
| cualquiera excepto Split=10 | TabAuth=7, `LastAct` | se inicia un tab auth | 17521 / csObject.cs:472 vía 17532 |
| cualquiera (no Split) | Check=4, `OrderID` | impresión de la cuenta del cliente en una mesa sin tab | 9959 / 9971 |
| cualquiera | Split=10, `OrderID=MainID`, `InUse=0` | Split por artículo; destino de un Join de split | 19820-19821; 14972 |
| cualquiera (con el control en pantalla) | CleanUp=6, `OrderID=NULL`, `InUse=0`, `LastAct` | finalización sin filas de split restantes; Void desde la cuadrícula; origen de Move/Join; cambio de tipo; trama huérfana/LRS; limpieza de `_OnComplete` | 18890/18923; frmGetOrders.cs:474; 14886/15014; 20055; 2954; 9377 |
| cualquiera (no Split) | CleanUp=6 (se conserva `OrderID`) | `OrderManager.VoidOrder` | OrderManager.cs:180-183 |
| cualquiera | Authorized=5 | finalización en la que la última forma de pago dejó un pago Auth (restaurante); `OrdersFPSplit.Status=5` en una mesa Split | 18902; 18868 |
| Split=10 | Status del subpedido restante, `OrderID`=ese pedido | finalización / `_OnComplete` con exactamente una fila `OrdersFPSplit` restante | 18895-18902; 9401-9424 |
| Open=1 / NULL | Status del origen / OrderID del origen | destino del Move | 14880 |
| CleanUp=6 | Open=1, `OrderID=NULL`, `LastAct`=reloj del cliente | Reset All | 19309-19325 |
| cualquiera | Open=1 o Busy=2, `OrderID=NULL` | menú emergente Available/Occupied (la fila de Orders no se toca) | 15190 |
| `InUse` 0 | 1 | recuperar un pedido sobre la mesa; clic en la mesa; "use this table?" | 8674; 15041; 15220 |
| `InUse` 1 | 0 (+`LastAct=GETDATE()`) | toda liberación: `Clear()`, cierre del formulario, comando del menú emergente, primera impresión de la cuenta, Reset All | 1001/1012; 2979; 15208; 9971; 19330 |
| `OrdersFPSplit.Status` cualquiera | Ordered=3 | `VoidAuth` de un pago que no es de tab | csPayment.cs:1124 |
| filas de `OrdersFPSplit` | eliminadas | Available/Occupied; Void desde la cuadrícula; Join sin split de un miembro de un split; finalización; borrado desde el editor | 15162/15179; frmGetOrders.cs:462; Lib.cs:5031-5062; 18877/18895; frmSetFloorMap.cs:617 |

## 11. Atomicidad y modos de fallo

1. **En la ruta legacy no se hace rollback de nada.** Las únicas transacciones son `OrderManager.VoidOrder` y `TakeAccountPayment`, más los dos procedimientos almacenados. Todo CommandText con varias sentencias hace commit sentencia a sentencia; como nunca se activa `XACT_ABORT`, un error de ejecución a nivel de sentencia dentro de un lote deja confirmadas las sentencias anteriores **y** ejecuta las posteriores, mientras que un error de compilación (parámetro no suministrado, objeto inexistente) hace fallar el lote entero antes de que se escriba nada. El fallo realista en una base de datos sin claves foráneas es un parámetro que falta (`@OrderID`/`@RefID`/`@CCTxID` en null tras un fallo anterior que se tragó).
2. **Residuo de un `SaveOrder` fallido sobre un pedido nuevo**: una fila de `Orders` (`Status=0`, `InUseID=TerminalID`, nunca liberado), su fila Create de `OrdersLog`, posiblemente la actualización de `FloorPlan`, un prefijo de líneas de `OrdersDetails` con el stock ya descontado — sin `OrdersTax`, `Payments` ni `OrdersTips`. Las líneas que no se llegaron a insertar conservan `ODID` en NULL en memoria y se insertan de nuevo en un guardado posterior que sí tenga éxito. Un fallo de wrapper en el INSERT de `Orders` aflora como una NRE en 2049, después del diálogo modal de error; el lote UPDATE del pedido existente puede perder `OpenPromoDetails` (DELETE confirmado, UPDATE fallido, NRE en 2133).
3. **El dinero se mueve antes que las filas en todos los flujos del procesador.** La aprobación de tarjeta (`Process`) precede a `SaveTrx`; la captura precede al UPDATE de `CCTxs` en `Complete`; el Void del dispositivo precede al lote `VoidAuth`; los Returns de reembolso, las llamadas `Activate/Redeem/AddValue` de tarjeta de regalo y las de Factor4 preceden a todas las filas de frmRefund (de la escritura 3 en adelante). Un fallo posterior al paso del procesador deja el dinero movido con, como mucho, una fila de `CCTxs` registrada. Dentro de `VoidAuth`, un fallo en la sentencia de `Orders` deja `CCTxs`/`Payments` anulados y el plano de salón reiniciado, pero `Orders.Paid` y `OrdersTips` intactos; el reinicio de `Terminals` se ejecuta igualmente porque el wrapper devolvió null.
4. **El commit por forma de pago se traga.** El batch A se ejecuta a través de `Lib.ExecNonQuery` (18036): ante una SqlException la memoria dice Paid, la base de datos sigue diciendo `Status=0` y la finalización continúa — el lote de cierre marca entonces el pedido como `Completed=1` con `Paid` incompleto. Los puntos de fidelización consumidos en 17907 no se restauran nunca, ni mediante `WithError` ni de ninguna otra forma.
5. **La finalización es asíncrona y se traga.** Si falla el batch C, `Orders.Status` se queda en 6 con `CloseDate` en NULL y sin fila Close de `OrdersLog`, pero las escrituras de fidelización/cliente/tarjeta de regalo (18700-18830) se ejecutan igualmente; en instalaciones con plano de salón el hilo desreferencia null en 18838. Cualquier excepción no manejada en ese hilo (`ArgumentOutOfRangeException` con un certificado de $0, `ExecScalar()` en null en 18690, división por cero en 18681 con un total de $0) llega a `AppDomain.UnhandledException` → ventana emergente → **terminación del proceso** (Start.cs:70-72). Conviene sondear `Status=1 AND CloseDate IS NOT NULL`; el `InUseID=NULL` del hilo de interfaz puede llegar antes.
6. **La compensación de `WithError` es parcial**: las filas de tipo tarjeta se borran lógicamente (`OrderID=-OrderID, Status=14`), las de Cash no; una propina CFD se mantiene en `Orders.Total` salvo con eGift; se pone `Orders.Status=6` cuando se pagó algo. Los fallos de certificado abortan antes de `WithError` (RowNotInTableException) y dejan la fila del certificado en `Status=0`.
7. **Rutas de aprobación parcial y de tab**: Cancel deja `Payments.Status=9` con `Amount=AuthAmount`, `CCTxs` anulado, `Orders.Paid`/`OrdersTips` intactos y `Terminals.OffDate` limpiado. Un Tab Complete fallido (`-1`) regresa con la fila del tab todavía en 11 y `TabAuthID` intacto, posiblemente tras poner a cero TD/TF (16956) e insertar una fila en `CcTxFailures`. En el lote 17486, un UPDATE de `Orders` que falla deja `CCTxs.Status=2` con `TabAuthID` en NULL y `Payments.Status=0`.
8. **Rutas offline**: `Offline(true)` persiste el modo offline en `Exatouch.cfg` **antes** de leer la tarjeta (17211; frmRefund.cs:1691) — sobrevive a los reinicios; un `SaveTrx` offline fallido hace entonces que el lote raw de 17255 (o frmRefund.cs:1719) lance "parameter @CCTxID not supplied". `Terminals.OffDate/OffSent` los limpia **cada VoidAuth online** (csPayment.cs:1173-1181) y cada fila anulada no offline de frmRefund (2846), con lo que se oculta el banner de offline mientras puede que sigan pendientes filas offline no relacionadas (pregunta abierta).
9. **`OrderManager.VoidOrder`** hace rollback de sus siete escrituras ante cualquier excepción (anulación rechazada con mensaje no vacío, pago eGift, `CCTxID` en NULL, fila de `CCTxs`/`Items` inexistente, Factor4 sin cliente, `FloorPlan.Status` en NULL), pero no de las anulaciones en el procesador, los abonos de tarjeta de regalo, el reembolso de Factor4 ni los comprobantes ya impresos; un reintento depende de que el procesador responda "already voided". Un terminal offline o un procesador sin inicializar produce una **anulación confirmada solo en local**. Sus bloqueos X bloquean a los lectores legacy mientras duran las llamadas al procesador/HTTP; los lectores nunca observan `Orders.Status=5` con los pagos todavía en 1/6.
10. **frmRefund**: si falla la escritura 3 → `mOrderID` en null → la escritura 4 lanza "parameter not supplied"; una sentencia raw que falle entre 2436 y 2854 aborta sin manejar y con las sentencias anteriores confirmadas; un Cancel dentro del try/catch del pago abandona los pagos ya anulados/reembolsados en el procesador antes en el bucle; un Re-create cuyo INSERT de cabecera falla deja bloqueado el original anulado. Reembolso abierto: `Process` tuvo éxito pero el INSERT de `Orders` falló → una fila Return de `CCTxs` sin pedido y, aun así, `Print.Refund(null)` y "Refund completed.".
11. **Join / Split / Bank swap** son cadenas de autocommits: un INSERT del pedido hijo del Split que falla continúa el bucle con `@OrderID` en NULL; una excepción lanzada entre el INSERT del hijo y el traslado de las líneas deja un hijo vacío; el `OrdersTax` del original ya está borrado (19622). Los fallos de Join dejan los orígenes movidos a medias y aun así devuelven `DialogResult.Yes`, de modo que el register reescribe el `Tax/Total` del destino (14060). El swap-out de Bank puede confirmar `Payments.BankID=NULL` mientras el bank sigue diciendo SwapIn.
12. **Bloqueos y estado obsoleto**: `InUseID` se asigna como última sentencia de `_LoadOrder` y lo limpia `Clear()` sin importar el Status; un terminal que muere lo deja asignado; frmRefund puede limpiar el bloqueo de uso de otro terminal tras una carga rechazada; borrar la última línea de un pedido recuperado, o cerrar frmRefund por cualquier vía que no sea Cancel/Clear, deja puesto el bloqueo de uso. `Global.MgrID` persiste durante toda la sesión tras cualquier autorización de manager y queda estampado en todas las filas Manager posteriores de `OrdersLog` y en `BankMod.MngrID`.
13. **Relojes**: `OrdersTax.CDate` (2640, 19710, frmRefund.cs:2514), `Log.CDate`, `GiftCards.DateSold`, `VoidDate` (OrderManager.cs:169), `Payments.CloseDate` en `VoidOrder` (154) y el `LastAct` de Reset All usan el `DateTime.Now` del **cliente**; todo lo demás usa `GETDATE()`. No se debe afirmar igualdad entre ambos.
14. **Concurrencia sobre `Global.SqlConnection`**: el hilo de finalización y el hilo de interfaz la comparten (MARS); el bucle de reconexión de `_SqlConnException` vuelve a ejecutar un CommandText indefinidamente si la causa no es la conexión.

## 12. Recetas de aserción de QA

Marcadores de posición: `@O` id del pedido (léase el número de pedido en pantalla, o `SELECT MAX(OrderID) FROM Orders` justo después del flujo), `@Term` = `SELECT TerminalID FROM Terminals WHERE MAC=<this MAC>` (o `Name=<NickName>`), `@User` = el `Staff.StaffID` del usuario conectado (1 en esta compilación DEBUG), `@Bank` = `Global.LocalSetting.BankID` de `Exatouch.cfg` (NULL cuando no hay ninguna sesión de cajón (bank) con swap-in), `@T0` = una instantánea de `GETDATE()` tomada antes del flujo. Hay que sondear la finalización (`Status=1 AND CloseDate IS NOT NULL`, hasta ~5 s) antes de aseverar nada escrito por el hilo en segundo plano. Donde la compilación DEBUG cambia un valor, se indica.

Los bloques de abajo no se pueden ejecutar tal cual se pegan: hay que declarar antes los marcadores de posición, o pasarlos con la sustitución `sqlcmd -v`.

```sql
DECLARE @O int = 245;                                                     -- el pedido bajo prueba
DECLARE @Term int = (SELECT TerminalID FROM Terminals WHERE MAC = '<this MAC>');
DECLARE @User int = 1;                                                    -- Staff.StaffID del usuario conectado (1 en esta compilación)
DECLARE @Bank int = NULL;                                                 -- LocalSetting.BankID de Exatouch.cfg, NULL si no hay sesión de cajón
DECLARE @I int = 100;                                                     -- una fila de Items con StockType=0 usada en el flujo
DECLARE @T0 datetime = '2026-09-07 21:00:00';                             -- capturado con GETDATE() antes de ejecutar el flujo
```

**Fije la configuración del laboratorio antes de fiarse de un valor esperado.** Cada uno de estos ajustes altera al menos una de las columnas de abajo: `Settings.Account.Merchant.IndustryType` (modo restaurante: las ventas con tarjeta son Auth → `Payments.Status=6`, `CCTxs.TxType=0`, Tab Auth/Complete visibles; modo retail: Sale → 1/2); `Printer.RemoteOption.Firing` y si existe o no una impresora remota/KDS (`OrdersDetails.Fire`/`Printed` NULL frente a 1 frente a 0/=Qty); `Global.TouchScreen` (= hay un CFD VGA conectado: `OrdersTips.Type` 1 frente a 7, el paso de propina en el CFD antes de leer la tarjeta, la doble suma de propina en `Complete`); `Account.TenderFee.Surcharge` (recargo falso del 3 % en toda venta con crédito en esta compilación); el estado del cajón en `Exatouch.cfg` (`Payments.BankID`, filas de `BankMod`); las ranuras de impuesto configuradas (filas de `OrdersTax` más allá de los Types 0-3); los ids de motivo 39/40/43 de `CustomFields`; y el rechazo de 1 de cada 33 del procesador falso en **todo** tipo de transacción — el arnés debe detectar el diálogo de rechazo y reintentar o limpiar tanto en ventas como en anulaciones, reembolsos y capturas.

### 12.1 Venta nueva en efectivo (retail)

IU: Register → categoría → artículo(s) → pestaña Payments → Cash → importe/entregado en `frmCash` → OK (o Exact Cash).

```sql
SELECT Status, Paid, Total, Tax, Tip, CloseDate, BatchID, InUseID, TerminalID, StaffID, OrderType, Printed, Source, TabAlert, RefundAmt, Surcharge
FROM Orders WHERE OrderID=@O;
-- Status=1 (sondear), Paid=Total, CloseDate>=@T0, BatchID=0 (NULL solo con un artículo con código de impuesto), InUseID NULL, TerminalID=@Term,
-- StaffID=@User, OrderType=0, Printed=0, Source=0, TabAlert=0, Tip=0.00, RefundAmt NULL, Surcharge NULL
SELECT Type, Status, Amount, Value, Tip, CloseDate, CloseUserID, CCTxID, BankID, TerminalID, UserID, RefundAmt, TDUsed, TFUsed, Reference, DeliveryFee
FROM Payments WHERE OrderID=@O;
-- 1 fila: Type=0, Status=1, Amount=Total, Value=lo entregado, Tip NULL, CloseDate>=@T0, CloseUserID NULL, CCTxID NULL, BankID=@Bank o NULL,
-- TerminalID=@Term, UserID=@User, RefundAmt=0, TDUsed=0, TFUsed=0, Reference NULL, DeliveryFee=0
-- Exact Cash puede añadir una segunda fila Type=0 con Amount=0 (pregunta abierta 6) — filtre Amount<>0 al contar
SELECT Type, ID FROM OrdersLog WHERE OrderID=@O ORDER BY Type;            -- (0,@User) y luego (1,@User)
SELECT ODID, LinePos, ItemID, Qty, Deleted, UserID, Printed, Fire, QtyDel, RefQty, PriceSell
FROM OrdersDetails WHERE OrderID=@O ORDER BY LinePos;                     -- LinePos 0..n-1, Deleted=0, UserID=@User, QtyDel/RefQty NULL;
--   Printed NULL y Fire NULL con Firing desactivado y sin impresora remota; Fire=1 con Firing activado y nada impreso; Fire=0 y Printed=Qty solo tras una impresión remota/KDS
SELECT Type, Qty, QtyExempt FROM OrdersTax WHERE OrderID=@O ORDER BY Type; -- Types 0,1,2,3 presentes; Type 0 Qty = ROUND(SUM(PriceSell*Qty) de las filas TaxableA,2)
SELECT QtyOnHand, QtySold FROM Items WHERE ItemID=@I;                      -- qty-anterior / anterior+qty (solo artículos StockType=0), ya cierto tras el paso Save
SELECT COUNT(*) FROM Payments WHERE OrderID=-@O;                            -- 0
```

### 12.2 Venta nueva con tarjeta

IU: artículos → Credit (o Debit) → diálogo de importe OK → dispositivo (falso en esta compilación; ~3 % de rechazos aleatorios → reintentar).

```sql
SELECT P.Type, P.Status, P.Amount, P.CCTxID, P.Reference, P.CloseDate, P.Tip,
       C.TxType, C.Status AS CCStatus, C.AType, C.AuthCode, C.CCNumber, C.CardHolder, C.ExpDate, C.Partial, C.TerminalID, C.BatchClose, C.CloseDate AS CCClose, C.TxRespons, C.Number
FROM Payments P JOIN CCTxs C ON C.CCTxID=P.CCTxID WHERE P.OrderID=@O;
-- Type=3 (también para Card on File), Status=1 (6 en modo restaurante), CCTxID NOT NULL, Reference NULL (batch A sobrescribió el AuthCode de SaveTrx), CloseDate>=@T0, Tip NULL,
-- C.TxType=2 (0 en modo restaurante), CCStatus=1 (0 en restaurante), AType=2 (0 en Debit), LEN(CCNumber)=8 empezando por '****', Partial=0,
-- C.TerminalID=@Term, BatchClose NULL, CCClose NULL, Number NULL; procesador falso: LEN(C.AuthCode)=6, CardHolder='Fake credit card', ExpDate='0130', TxRespons NULL;
-- con Account.TenderFee.Surcharge activado: C.Surcharge=P.Surcharge=ROUND(amount*0.03,2) y Orders.Total/Surcharge lo incluyen
SELECT Status, Paid, CloseDate FROM Orders WHERE OrderID=@O;              -- 1, importe, NOT NULL (Completed también mientras P.Status=6 en modo restaurante)
SELECT AuthDate, Sent FROM Terminals WHERE TerminalID=@Term;              -- AuthDate NOT NULL tras la primera aprobación online de Credit, Sent NULL
SELECT COUNT(*) FROM CCTxFailures WHERE OrderID=@O;                       -- 0 (>=1 por cada rechazo)
```

### 12.3 Pedido en espera recuperado y pagado

IU: artículos → **Save** → Get Order → seleccionar → OK → Cash → OK.

```sql
-- tras Save
SELECT Status, InUseID, Paid, Printed, Total FROM Orders WHERE OrderID=@O;         -- 0, NULL, NULL, 0, total
SELECT COUNT(*) FROM Payments WHERE OrderID=@O;                                    -- 0
SELECT Type, ID FROM OrdersLog WHERE OrderID=@O;                                   -- exactamente (0,@User)
SELECT QtyOnHand FROM Items WHERE ItemID=@I;                                       -- ya decrementado (el stock se mueve en Save, no al pagar)
-- tras Get Order, antes de pagar
SELECT InUseID FROM Orders WHERE OrderID=@O;                                       -- @Term
-- tras Cash (el clic en la forma de pago vuelve a ejecutar SaveOrder porque la cuadrícula activa Modified — sección 3.1)
SELECT Status, InUseID, Paid, Printed FROM Orders WHERE OrderID=@O;                -- 1 (sondear), NULL, Total, 0
SELECT COUNT(*) FROM OrdersTax WHERE OrderID=@O AND Type IS NOT NULL;              -- mismo recuento que tras Save (OTIDs reutilizados FIFO)
SELECT Type, ID FROM OrdersLog WHERE OrderID=@O ORDER BY Type;                     -- (0,@User),(1,@User) — sin segunda fila Create
```

### 12.4 Split por artículo

IU: pedido con ≥2 líneas (guardado o no) → Split → confirmar los diálogos de propinas/promoción → en `frmSplitItem` mover líneas al grupo 1 → OK. `@Orig` = el original, `@Child` = `SELECT MAX(OrderID) FROM OrdersLog WHERE ID=@User AND Type=0`.

```sql
SELECT OrderID, Status, Paid, Total, Tax, Tip, Exchange, CustExempt, StaffID, CustomerID, OrderType, InUseID
FROM Orders WHERE OrderID IN (@Orig, @Child);
-- hijo: Status=Status del original (0), Paid NULL, Exchange NULL, CustExempt NULL, StaffID/CustomerID/OrderType copiados, InUseID NULL; cada Total = sus propias líneas
SELECT OrderID, COUNT(*) AS Lines, SUM(CASE WHEN Shared=1 THEN 1 ELSE 0 END) AS ShareRows
FROM OrdersDetails WHERE OrderID IN (@Orig, @Child) AND Deleted=0 GROUP BY OrderID;  -- las líneas movidas llevan OrderID=@Child y su CDate ORIGINAL
SELECT OrderID, Type, Qty, QtyExempt FROM OrdersTax WHERE OrderID IN (@Orig, @Child) ORDER BY OrderID, Type;  -- una fila por ranura gravada y por subpedido
SELECT Type, ID FROM OrdersLog WHERE OrderID=@Child;                                  -- (0,@User)
SELECT COUNT(*) FROM OrdersTips WHERE OrderID=@Orig AND Type<>2;                       -- 0 (se borran las propinas no de grupo)
-- solo con plano de salón:
SELECT OrderID, Status FROM OrdersFPSplit WHERE FPID=@FPID;                            -- una fila por subpedido, Status=3 (7 para @Orig con un Tab Auth)
SELECT Status, InUse, OrderID FROM FloorPlan WHERE FPID=@FPID;                         -- 10, 0, @Orig
```

### 12.5 Tab Auth y después Tab Complete con propina (modo restaurante)

IU: mesa/pedido → Tab Auth → tarjeta → (frmTabLimit) → etiqueta → vuelve al plano de salón. Más tarde: recuperar → Tab Complete → diálogo de propina (sin multitouch: `frmMessageValue`; con multitouch: CFD) → listo.

```sql
-- tras Tab Auth
SELECT O.Status, O.Paid, O.TabAuthID, O.TerminalID, O.TabMin, O.TabLimit, O.Label, O.InUseID,
       P.Status AS PayStatus, P.Value, P.CCTxID, P.Reference, P.CloseDate,
       C.Status AS CCStatus, C.TxType, C.AType, C.Partial, C.CloseDate AS CCClose
FROM Orders O JOIN Payments P ON P.PayID=O.TabAuthID JOIN CCTxs C ON C.CCTxID=P.CCTxID WHERE O.OrderID=@O;
-- Status=0, Paid=0 en un carrito recién guardado (NULL en un pedido en espera recuperado: el re-guardado mantiene NULL, 2113), TabAuthID=P.PayID, TerminalID=@Term, TabMin=<lo introducido>, TabLimit NULL (AuthAmount si hay aprobación parcial), Label con valor, InUseID NULL;
-- PayStatus=11, Value=TabMin, CCTxID NOT NULL, Reference=AuthCode, CloseDate NULL; CCStatus=2, TxType=0, AType=2, Partial=0, CCClose NULL
SELECT Status FROM FloorPlan WHERE FPID=(SELECT FPID FROM Orders WHERE OrderID=@O);   -- 7 (10 si la mesa se dividió)
-- tras Tab Complete con propina @Tip (aseverar después de Status=1)
SELECT O.TabAuthID, O.Status, O.Paid, O.Tip, O.Total, P.Status AS PayStatus, P.Amount, P.Tip AS PayTip, P.CloseDate, P.CloseUserID,
       C.Status AS CCStatus, C.TxType, C.Tip AS CCTip, C.CloseDate AS CCClose,
       (SELECT COUNT(*) FROM OrdersTips WHERE PayID=P.PayID) AS TipRows
FROM Orders O JOIN Payments P ON P.OrderID=O.OrderID AND P.Status<>14 JOIN CCTxs C ON C.CCTxID=P.CCTxID WHERE O.OrderID=@O;
-- TabAuthID NULL, Status=1, O.Tip=@Tip, O.Total=total antes de propina+@Tip, PayStatus=1, CloseDate NOT NULL, CCStatus=1, TxType=1, CCTip=@Tip (NULL cuando es 0), CCClose NOT NULL, TipRows=1;
-- sin multitouch (Complete(pID,null)): P.Amount = saldo autorizado + @Tip (+@Tip otra vez con Global.TouchScreen — pregunta abierta), P.CloseUserID=O.StaffID, O.Paid incluye la propina;
-- con multitouch: P.Amount sale de la fila del register, CloseUserID NULL; OrdersTips.Type = 1 (CC) o 7 (CFD) según Global.TouchScreen
```

### 12.6 Anulación desde el register (pantalla Refund → Void) de un pedido con tarjeta completado

IU: Register → Refund → Find Order → seleccionar `@O` → todas las líneas marcadas → Void → confirmar → mensaje de éxito del estilo "Transaction voided" → (Re-create sin marcar).

```sql
SELECT o.Status, o.VoidDate, o.RefundAmt, o.Paid, o.Tip, o.CloseDate, o.InUseID,
       (SELECT COUNT(*) FROM Orders WHERE RefID=@O) AS RefundOrders,
       p.Status AS PayStatus, p.RefundAmt AS PayRefund, p.CloseDate AS PayClose,
       c.Status AS CCStatus, c.TxType, c.RAmt, c.Tip AS CCTip,
       (SELECT COUNT(*) FROM OrdersTips WHERE OrderID=@O) AS TipRows
FROM Orders o JOIN Payments p ON p.OrderID=@O AND p.Amount>0 AND p.Type NOT IN (2,5,15) AND p.Status<>14   -- las filas Coupon/Discount/ClubDiscount siguen en Status=1 y nunca se anulan
LEFT JOIN CCTxs c ON c.CCTxID=p.CCTxID WHERE o.OrderID=@O;
-- Status=5, VoidDate>=@T0, RefundAmt=Paid, Paid/Tip/CloseDate sin cambios, InUseID NULL, RefundOrders=0,
-- PayStatus=9, PayRefund=p.Amount, PayClose>=@T0 (tarjetas), CCStatus=3, TxType=6, RAmt=p.Amount, CCTip=0, TipRows sin cambios
SELECT OffDate, OffSent FROM Terminals WHERE TerminalID=@Term;                        -- NULL, NULL
SELECT QtyOnHand FROM Items WHERE ItemID=@I;                                          -- anterior + Qty (StockType<>2)
SELECT COUNT(*) FROM OrdersLog WHERE OrderID=@O AND Type IN (2,6);                    -- 0 (frmRefund no registra nada)
```

Variante — `OrderManager.VoidOrder` (solo se alcanza cancelando el diálogo de activación de eGift al completar un pedido que vende una tarjeta de regalo): espérese además `Payments.RefundAmt=Amount` y `CloseDate` en **todos** los pagos Completed/Auth, incluido el efectivo, `Items.QtyOnHand` incrementado por cada línea **incluidos los pseudo-artículos y los modificadores**, `FloorPlan.Status=6` conservando `OrderID`, una fila de `GiftCards` con `FromRefund=1` cuando se usaron formas de pago Certificate/Reward, y ninguna fila de `OrdersLog`; `Orders.CloseDate/Paid/Total` sin cambios.

### 12.7 Anular / eliminar un pedido abierto desde la lista de recuperación

IU: Register → Get Order → seleccionar un pedido Open (sin tab) → Void → confirmar (el diálogo de motivo solo aparece cuando `RefReqReason`).

```sql
SELECT Status, ReasonID, Note, CloseDate, VoidDate, RefundAmt, InUseID FROM Orders WHERE OrderID=@O;
-- 5, 40 (o el motivo elegido), NULL (o la nota), CloseDate>=@T0, VoidDate NULL, RefundAmt sin cambios, InUseID sin cambios
-- Orders.Note es una columna text: compararla con CAST(Note AS varchar(max)) o LIKE, nunca con '=' sobre la columna en sí
SELECT Type, ID FROM OrdersLog WHERE OrderID=@O AND Type IN (2,6) ORDER BY Type;   -- (2, 1 en esta compilación DEBUG / NULL en Release sin override), (6,@User)
SELECT COUNT(*) FROM Payments WHERE OrderID=@O AND Status=0;                     -- 0 (las filas Open ahora son 14; las filas Completed mantienen 1)
SELECT QtyOnHand, QtySold FROM Items WHERE ItemID=@I;                             -- valor anterior + Qty, QtySold sin cambios
SELECT Status, InUse, OrderID FROM FloorPlan WHERE OrderID=@O;                     -- ninguna fila (la mesa queda con Status=6, OrderID NULL)
```

### 12.8 Reembolso total de un pedido completado con una sola tarjeta

IU: Refund → Find Order → `@O` → todas las líneas marcadas (por defecto) → Refund → (motivo) → Return en el dispositivo → "Refund completed." `@R` = `SELECT MAX(OrderID) FROM Orders WHERE RefID=@O` (cada reembolso parcial del mismo pedido añade otra fila con `RefID=@O`, así que nunca use una subconsulta escalar a secas).

```sql
SELECT o.Status, o.RefundAmt, o.Paid, o.InUseID, r.OrderID, r.RefID, r.Status AS RStatus, r.Total, r.Paid AS RPaid, r.Tax, r.CloseDate, r.ReasonID, r.StaffID, r.TerminalID
FROM Orders o JOIN Orders r ON r.RefID=o.OrderID WHERE o.OrderID=@O;
-- exactamente una fila: o.Status=1, o.RefundAmt=o.Paid, o.InUseID NULL, RStatus=4, r.Total=r.Paid=-o.Paid, r.Tax=-o.Tax, CloseDate>=@T0, ReasonID=39 o el elegido, StaffID=o.StaffID, TerminalID=@Term
SELECT p.PayID, p.Status, p.RefundAmt, np.Amount, np.Status AS NStatus, np.Type, np.OrderID, np.RefID, np.CloseUserID, c.TxType, c.Status AS CCStatus, c.Tip
FROM Payments p JOIN Payments np ON np.OrderID=@R AND np.Type=p.Type LEFT JOIN CCTxs c ON c.CCTxID=np.CCTxID
WHERE p.OrderID=@O AND p.Amount>0 AND p.Type NOT IN (2,5,15) AND p.Status<>14;   -- frmRefund oculta las filas Coupon/Discount/ClubDiscount y nunca se reembolsan
-- p.Status=1 (sin cambios), p.RefundAmt=p.Amount; np.Amount=-p.Amount, NStatus=8, np.Type=p.Type, np.OrderID=np.RefID=@R, CloseUserID=@User; c.TxType=5, CCStatus=1
SELECT Type, ID FROM OrdersLog WHERE OrderID=@R ORDER BY Type;                       -- (0,@User),(1,@User),(2, 1 aquí / NULL en Release)
SELECT d.ODID, d.Qty, d.RefQty, m.Qty AS MirrorQty, m.RefQty AS MirrorRefQty
FROM OrdersDetails d JOIN OrdersDetails m ON m.OrderID=@R AND m.ItemID=d.ItemID AND m.Description=d.Description WHERE d.OrderID=@O AND d.Deleted=0;
-- d.RefQty=d.Qty, MirrorQty=-d.Qty, MirrorRefQty NULL  (el join multiplica en cruz cuando el mismo artículo está en dos líneas; en ese caso añada PriceSell a la cláusula ON)
SELECT Type, Qty FROM OrdersTax WHERE OrderID=@R;                                     -- cantidades de las ranuras negadas
SELECT QtyOnHand, QtySold FROM Items WHERE ItemID=@I;                                 -- QtyOnHand anterior + Qty, QtySold sin cambios
```

### 12.9 Reembolso parcial (una unidad de una línea, pedido en efectivo)

IU: igual que 12.8 pero poniendo la cantidad de la línea a 1 (o desmarcando las demás líneas) → Refund.

```sql
SELECT o.Status, o.RefundAmt, o.Paid, r.Total, r.Paid AS RPaid, r.Status AS RStatus
FROM Orders o JOIN Orders r ON r.RefID=o.OrderID WHERE o.OrderID=@O;                -- 1, parte reembolsada (< Paid), sin cambios, -parte reembolsada, igual, 4
SELECT d.RefQty, d.Qty, m.Qty AS MirrorQty FROM OrdersDetails d JOIN OrdersDetails m ON m.OrderID=(SELECT MAX(OrderID) FROM Orders WHERE RefID=@O) AND m.ItemID=d.ItemID
WHERE d.ODID=@ODID;                                                                   -- 1, sin cambios, -1
SELECT p.RefundAmt, np.Amount, np.Status, np.Type FROM Payments p JOIN Payments np ON np.OrderID=(SELECT MAX(OrderID) FROM Orders WHERE RefID=@O)
WHERE p.OrderID=@O AND p.Type NOT IN (2,5,15) AND p.Status<>14;                                                                   -- parte reembolsada, -parte reembolsada, 8, 0
-- un segundo reembolso parcial crea un SEGUNDO pedido de reembolso (RefID=@O) y suma a RefundAmt/RefQty; una línea reembolsada por completo desaparece de la cuadrícula de reembolso
```

### 12.10 Pay Out (cajón de dinero)

IU: carrito vacío → Pay In/Out → Pay Out → fila de motivo → importe 25.00 → nota → OK.

```sql
SELECT o.OrderType, o.Status, o.Total, o.Tax, o.Paid, o.RefID, o.CloseDate, o.StaffID, o.TerminalID,
       p.Type AS PayType, p.Amount, p.Status AS PayStatus, p.Reference, p.BankID, p.RefID AS PayRefID, p.CloseDate AS PayClose,
       (SELECT COUNT(*) FROM OrdersLog l WHERE l.OrderID=o.OrderID) AS LogRows,
       (SELECT COUNT(*) FROM OrdersDetails d WHERE d.OrderID=o.OrderID) AS DetailRows
FROM Orders o JOIN Payments p ON p.OrderID=o.OrderID WHERE o.OrderID=@O;
-- 7, 1, 0, 0, NULL, @CustomID, >=@T0, @User, @Term; 9, -25.00, 1, <nota>, @Bank o NULL, @CustomID, >=@T0; LogRows=2 (Types 0 y 1), DetailRows=0
-- Pay In: OrderType=6, PayType=8, Amount=+25.00
SELECT Cash, UserID, MngrID, Notes FROM BankMod WHERE BankID=@Bank AND Cash=-25.00 AND [Date]>=@T0;   -- solo cuando @Bank está definido: una fila, @User, MngrID (1 aquí), Notes=<nota>
-- (BankMod también recibe filas de ajuste de turno desde frmBank.cs:290, así que nunca haga aserciones con TOP 1 ORDER BY Date; Notes es una columna text: use CAST(Notes AS varchar(max)) para comparar)
```

### 12.11 Ajuste de propina tras el cierre (`frmReView`) y Complete Auth con propina

IU (ajuste): Register → Complete → Review → seleccionar la fila de la tarjeta → propina nueva → OK. `@P` = PayID, `@New` = propina nueva, `@Old` = propina anterior (0 si no había).

```sql
SELECT O.Paid, O.Tip, O.Total, P.Amount, P.Tip AS PayTip, C.Tip AS CCTip, C.TxType, T.Type AS TipType, T.Amount AS TipAmt, T.Value, T.StaffID
FROM Payments P JOIN Orders O ON O.OrderID=P.OrderID JOIN CCTxs C ON C.CCTxID=P.CCTxID LEFT JOIN OrdersTips T ON T.PayID=P.PayID
WHERE P.PayID=@P;
-- respecto al estado previo: O.Paid/O.Tip/O.Total cada uno +(@New-@Old); P.Amount +(@New-@Old); PayTip sin cambios; CCTip=@New (NULL si 0); TxType=4;
-- TipType=1 con TipAmt=Value=@New cuando @Old era NULL; ninguna fila de OrdersTips para @P cuando @New=0; StaffID=O.StaffID
```

IU (Complete Auth, restaurante, `Payments.Status=6`): Register → Complete → seleccionar la fila Authorized → OK → diálogo de propina `@Tip`.

```sql
SELECT C.TxType, C.Status, C.CloseDate, C.Tip, P.Status AS PayStatus, P.Amount, P.CloseUserID, O.StaffID, O.Paid, O.Tip, O.Total, T.Type, T.StaffID AS TipStaff, T.Amount
FROM Payments P JOIN CCTxs C ON C.CCTxID=P.CCTxID JOIN Orders O ON O.OrderID=P.OrderID LEFT JOIN OrdersTips T ON T.PayID=P.PayID
WHERE P.PayID=@P;
-- C.TxType=1, C.Status=1, C.CloseDate NOT NULL, C.Tip=@Tip (NULL si 0), PayStatus=1, P.Amount=importe autorizado+@Tip (+@Tip otra vez con Global.TouchScreen),
-- P.CloseUserID=O.StaffID, O.Paid/O.Tip/O.Total cada uno +@Tip, T.Type=1 (7 con un CFD; nunca 6, ni siquiera en una autorización eGift), TipStaff=O.StaffID, T.Amount=@Tip
```

### 12.12 Reembolso en efectivo de importe libre y filas de pago en mesa (dispositivo)

```sql
-- Refund → Open → importe @X → Cash → confirmar
SELECT TOP 1 OrderID, Status, OrderType, RefID, ReasonID, StaffID, TerminalID, BaseAmount, Paid, Total, Tax, TDUsed, TFUsed, CloseDate, InUseID
FROM Orders ORDER BY OrderID DESC;   -- Status=10, OrderType=0, RefID NULL, ReasonID=39/el elegido, @User, @Term, -@X, -@X, -@X, 0, 0, 0, >=@T0, NULL
SELECT Type, Status, Amount, CCTxID, OrderID, RefID, UserID, CloseUserID FROM Payments WHERE OrderID=@RefOrderID;  -- 0, 13, -@X, NULL, @RefOrderID, NULL, @User, @User
SELECT ItemID, CatID, SubCatID, Description, Price, PriceSell, Qty, RefQty FROM OrdersDetails WHERE OrderID=@RefOrderID;  -- 1, 1, 1, 'Open Ended Refund', @X, @X, -1, 1
SELECT COUNT(*) FROM OrdersTax WHERE OrderID=@RefOrderID;                                                             -- 0
-- Pago en mesa: las filas con MachName='Clover Flex' NO deben aparecer salvo que haya desplegado un dispositivo Flex (pregunta abierta 3)
SELECT p.Type, p.Status, p.MachName, p.BankID, p.TerminalID, o.PaidAtTable, o.Status FROM Payments p JOIN Orders o ON o.OrderID=p.OrderID WHERE p.PayID=@PayId;
-- 3, 1, 'Clover Flex', NULL, NULL, 1, 1 o 6
SELECT Type, Amount FROM OrdersTips WHERE PayID=@PayId;                                                               -- una fila, Type=5, incluso con propina 0
```

## 13. Preguntas abiertas / sin resolver (deduplicadas)

Puntos que plantearon los fragmentos y que el código por sí solo no permitió resolver, unificados entre fragmentos. Resueltos durante la redacción (no se repiten más abajo): un clic de forma de pago sobre un pedido recuperado sin modificar **sí** establece `Modified` y ejecuta `SaveOrder` (csDataGridView.cs:927-933, csForm.cs:824); `Orders.InUseID` guarda `Global.TerminalID`, no un id de usuario (2034, 8925); el stock se decrementa dentro de `SaveOrder`, no al completar (2473-2485); los enlaces de `OrdersLog.Type` de split/re-creación/reparto son Create/Create/Close (19694, frmRefund.cs:3013, frmDelManager.cs:643); el lote de finalización no toca `ItemTotal` (18637-18642); el UPDATE de re-guardado de `SaveOrder` sí reescribe `Orders.FPID` (2093); los pedidos de reparto pagados con tarjeta los cierra el lote de finalización al aplicar la forma de pago (18637, también cuando los pagos son Auth=6); las formas de pago con tarjeta de A/R sí escriben una fila `CCTxs` referenciada por `AccountPayments.CCTxId` (csPayment.cs:93-104 con `PayID=0`); `Security.Module.Main` = 20 (csSecurity.cs:28).

**Entorno / compilación**
0. **No hay commit de referencia para el código fuente de la aplicación.** `2.0.x`, `Exatouch.Entities` y `Exatouch.Repositories` no son repositorios git en esta máquina; los números de línea están atados al árbol tal como se leyó el 7 de septiembre de 2026 (binario compilado el 5 de junio de 2026). Registre una suma de comprobación o un archivo del árbol junto con cualquier prueba que fije estos anclajes.
1. **Qué binario ejecuta el laboratorio de QA.** El proyecto versionado solo compila `DEBUG;CFD`: procesador falso (rechazos aleatorios ~3 %, `TxRespons` NULL, `CardHolder='Fake credit card'`), sin controles de PIN ni filas `Log` de "Manager PIN Request", `MgrID=UserID=1`, cajón de dinero sin efecto, cierre de lote al estilo Training, `frmOfflineProcess` inalcanzable. Un binario Release/AUTOQA cambia toda expectativa que dependa del procesador y añade los diálogos modales de seguridad.
2. **Tablas ausentes de `db-schema.txt`**: `OrdersLog`, `Items`, `ItemsTx/ItemsTxDetails`, `Customers`, `Appointments`, `Bank/BankMod/BankDetails`, `CustomFields`, `Log`, `OpenPromoDetails/OrdersPromoDetails`, `FreshSheet`, `Staff`, `Config`, `Tax`, `CCTxFailures` (el scout lista `CCtxFailures`; `Source` recibe un enum en csPayment.cs:63 y un bit en :694, y `CCNumber` un PAN completo en :681 — riesgo de truncamiento). Confirme columnas, identidad/valores por defecto y nombres antes de hacer aserciones; la forma `OrdersLog(OLID, CDate, ID, OrderID, Type)` proviene de una lectura puntual de INFORMATION_SCHEMA.
3. **El llamador externo de pago en mesa** (`spApplyPayment`/`spTipAdjust`, "Clover Flex") no está en el repositorio; si no hay ningún dispositivo desplegado, nunca deben aparecer filas con `MachName='Clover Flex'`.
4. **Flags de tiempo de ejecución** que condicionan escrituras: `License.FloorPlanEnabled`, `Preference.Security.OpenAlwaysCD`, `Global.TouchScreen` (= `CFD.IsVGA`; conmuta `OrdersTips.Type` CC=1/CFD=7 y habilita la doble suma de propina), un procesador real frente a `Global.IsConnected` (VoidOrder pasa a ser solo local cuando no hay conexión), `Global.Tax.TaxID`, `Global.LocalSetting.BankID` (persistido en `Exatouch.cfg` — un bank intercambiado en una sesión anterior sigue estampando `Payments.BankID`), `_OTSelected.ID` para los tipos de pedido integrados (`Orders.CustomID` se espera NULL), `Register.Tip.Tips`, precio dual.
5. Se asume que las filas de `CustomFields` con `CustomID` 39 (motivo de reembolso), 40 (anulación), 43 (cliente de pay-out) existen en todas las XTTDB.

**Register: pago/finalización**
6. Exact Cash prepara una segunda fila Cash (16430-16435): ¿queda una fila `Payments` de importe cero después de `SaveOrder`?
7. Propina doble en cajas registradoras con pantalla táctil: `Complete(pID, null)` suma la propina a `Payments.Amount` en csPayment.cs:496-500 y de nuevo en 563-569 cuando `Global.TouchScreen` (tipos de pedido no multitouch como Delivery/Custom; cierre con crédito desde el gestor de reparto, filas 65+70).
8. Tab Complete, sin multitouch, con recargo activado y comisión por forma de pago desactivada: 17061 sobrescribe `Payments.Amount` con el importe en memoria (sin la propina que `Complete` acaba de añadir) más el recargo, y batch A omite `Amount` para las filas de tab — la propina puede desaparecer de `Payments.Amount`.
9. `Orders.Total` conserva una propina CFD después de que la tarjeta sea rechazada (solo eGift la revierte, 19012-19035); confirme que ningún guardado posterior la reescribe.
10. Filas de canje de certificado (forma de pago Certificate negativa): `CustomValue.Description` nulo asignado a `mRow["SKU"]` en 18613 — `ArgumentNullException` en el hilo de interfaz frente a DBNull → `PriceOriginal` DBNull → `CreateGiftCertificate(0)` lanzando en el hilo en segundo plano; hace falta comprobarlo en ejecución.
11. Escenario real que produce `mDue<0` en 18307 (fila sintética `Type=0, Amount<0` más una fila PayOutCust negativa) y el `Orders.Paid` resultante.
12. La forma de pago Reward sobre un total de $0 divide por cero en el hilo de finalización (18681, `mTotal` no se refresca tras la reescritura de DP).
13. `FloorPlan.StaffID` se pone a null en memoria (18917/9387) pero `DBUpdate` nunca lo persiste.
14. `_Payments.Compute("SUM([TFUsed])")` (12645) incluye filas con `Deleted=true`: ¿supresión intencionada de la comisión o un error (visible tras `_ResetPay` en un pedido recuperado con filas de comisión)?
15. Qué flujo elimina la INVOICE de CRM creada durante la preparación (15733) cuando `btnProcessClick` aborta por un motivo distinto de un fallo de SaveOrder / `UpdateTransactionOrderId`.
16. `_CancelGiftCertificatePayment` (4655) también elimina de la cuadrícula y de `_Paid` las filas de certificado ya Completed de un pedido recuperado, mientras que `[Payments]` las conserva.
17. La línea 12000 suma la comisión por forma de pago dual al acumulador I previo a impuestos sin ninguna guarda `IsDualPrice`; queda por saber si puede existir una comisión por forma de pago de cuenta distinta de cero con el precio dual desactivado (en cuyo caso los Types 26-29 de `OrdersTax` quedan sobrevalorados). No se han rastreado las fórmulas de precio dual (`DualPriceOrderModel.TotalUpdate`, csDualPriceManager.cs:1031+) ni los auxiliares `IsDualPriceTriggerTender/ActiveTender`; que `GetDualPriceAmount` devuelva (0,0) ante cualquier excepción hace que `_Total = tip + other` de forma silenciosa.
18. Unidades de los acumuladores de impuesto en dólares por unidad (`TaxBDollar/C/D/H/I`) y si Tax A puede ser en dólares; que `TaxValue[i]` sean fracciones se apoya en frmTaxes.cs:259-262.
19. Tab Auth fuera del modo restaurante enviaría `Sale=2` y guardaría `CCTxs.Status=1` antes de cambiar a 2 — confirme que los botones solo existen en modo restaurante.
20. La guarda del setter `csObject.Status` (csObject.cs:412) y `UpdateAll`: qué tipos/estados de mesa hacen que la escritura de `FloorPlan` del pedido nuevo conserve los antiguos Status/InUse, y si se dispara la difusión por socket `Ordered`.
21. Semántica de `Global.IsLock` (csGlobal.cs:99) detrás de las salidas anticipadas en 1759-1775; la ruta de interfaz que llega a `_PrintOrder` en 2805 en un pedido de tab-auth recién creado.
22. `Payments.BankID` cuando no existe sesión de cajón (bank): 16155 comprueba `null` de C#, 16161 enlaza `?? DBNull` — confirme que se omite `BankMod` y que `BankID` queda NULL.

**Register: otros**
23. El bloque `OrdersLog` de ApprovedBy para pedidos existentes (2138-2145) es código muerto: volver a guardar tras una aprobación de edad legal no registra ninguna fila `ApprovedBy=5` — ¿defecto o intención?
24. Undo Exchange no encola el ODID de la fila de etiqueta (13876-13877): guardar → deshacer → guardar debería dejar una fila `ItemID=19` residual.
25. Eliminar la última línea de un pedido recuperado fuerza `Modified=false` y omite `Clear()` (13630-13635): ¿cuándo se persisten los borrados lógicos encolados y se libera el bloqueo de uso?
26. Split con un INSERT hijo fallido (`@OrderID` NULL) — ¿SqlClient rechaza el nulo o aparecen filas de `OrdersLog/OrdersTax/OrdersDetails` con `OrderID NULL`? Los hijos del Split no heredan `Exchange/CustExempt/Surcharge/TDUsed/TFUsed` y el `Exchange` del original no se recalcula (19771) — ¿es intencionado?
27. Botón Refund con un carrito modificado que nunca se guardó y "No" visible: frmRefund se abre con el carrito todavía en memoria — ¿qué ocurre con él?
28. Rama de miembro de un split de `Lib.JoinOrder` (Lib.cs:5049-5056): con una sola fila de split restante y `mFPID==pFPID`, elimina la fila `OrdersFPSplit` **del destino** y pone la mesa en Ordered — ¿es un colapso intencionado? La actualización de `Payments` en Lib.cs:5082 no tiene filtro por OrderID (afecta a toda la base de datos).
29. Recuperación tras la NRE en 9939 (fallo de `Printed=1`); comportamiento de `csPrint.OrderDetails` para `ReceiptAndRemote=4`/`StickyAgain=3`; otros llamadores de `SearchOrder` (¿escaneo en frmMain?).
30. Divergencia entre `Global.Register.StaffID` y `Global.UserID` tras Take/Reassign en el plano de salón (`Orders.StaffID` y el `ID` de la fila Create pueden diferir de `Payments.UserID`) — es por diseño tras Reassign (19255); confírmelo para Take.

**Refund**
31. La escritura 14 de frmRefund (`FloorPlan.Status=6`) se considera código muerto según la semántica de SqlClient; confirme que anular un pedido de mesa deja `FloorPlan.Status` sin cambios.
32. `Default.ResponseMessage` solo se limpia ante un rechazo (csPayment.cs:896); un mensaje obsoleto puede hacer que frmRefund.cs:2255 lance en un `VoidAuth` que falló de forma silenciosa (sin conexión, comprobaciones de gift). Que PAX/Dejavoo devuelvan el literal "already voided" al volver a anular (OrderManager.cs:85) decide si un `VoidOrder` parcialmente fallido puede reintentarse.
33. `Activate` con importe cero en frmRefund.cs:2139 — ¿tiene algún efecto en el proveedor? No se rastrearon más allá los efectos remotos de eGift ni el simulador de Training.
34. `Orders.OtherRefunded` no tiene ningún escritor en el código rastreado (lector en frmRefund.cs:323).
35. La comprobación de RefDays en frmRefund.cs:395 nunca se dispara para pedidos antiguos (solo filtra Find Order) — ¿es intencionado?
36. El reembolso abierto en modo offline con Debit/EBT se cuela y escribe `Payments.Type=4, CCTxID NULL` sin transacción de tarjeta (1684-1688); el Credit offline con un `SaveTrx` fallido lanza "parameter @CCTxID not supplied" en 1719 después de haber persistido el modo offline; los procesadores no semi-integrados leen `OCCNumber` (csPayment.cs:753), que el `CardData` del reembolso abierto nunca establece (NRE); qué devuelve el procesador del laboratorio para `Return` con `OrderID=0`; la semántica del tope `frmMessageValue.MaxValue`; el recorte de `dgvDetailsCellEndEdit` se omite para las filas de reembolso sin recibo (3150).
37. El rechazo de un Return offline mueve `Orders.Paid` en la dirección equivocada (`Paid-=ABS(Amount)`, frmOfflineProcess.cs:200); queda por saber si los reembolsos offline son alcanzables.

**csPayment / Managers / satélites**
38. `Terminals.OffDate/OffSent` se limpian en **cada** `VoidAuth` online (csPayment.cs:1173-1181) y en cada fila anulada no offline de frmRefund (2846) — ¿es intencionado, u oculta transacciones offline pendientes?
39. `Complete` para pagos de gift/offline sobrescribe `CCTxs.PNRef`/`TransmitID` con '' (csPayment.cs:483-491); queda por saber si un sistema en producción llega ahí con una fila offline.
40. Los viajes de ida y vuelta de fila completa que hace Dapper con `CCTxs.Number` (varbinary) y `Sign` (image) en `VoidOrder`/`UpdateNumber`: confirme que una fila de tarjeta anulada conserva sus bytes de `Sign`.
41. `OrderManager.VoidOrder` incrementa `QtyOnHand` de los pseudo-artículos (ItemID 1-12, StockType=2) y de los modificadores (sin filtro de StockType), mientras que frmRefund y frmGetOrders filtran `StockType<>2` — impacto desconocido en los informes.
42. La finalización en efectivo de un reparto suma `frmComplete.Tip` (precargada desde `Orders.Tip`, de solo lectura para pedidos online) a `Orders.Total/Paid` y a `Payments.Amount` — un pedido que ya llevaba propina la cuenta dos veces salvo que se borre; además, esa rama no hace ninguna llamada al cajón de dinero ni al recibo.
43. La ruta posterior a "Ignore" en un `ExecReader` de tipo wrapper (lector nulo → NRE en `.HasRows`) está inferida, no observada.
