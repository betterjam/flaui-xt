# How Exatouch (XT) inserts a new order and updates it: pay, complete, void, refund, tips, split, pay in/out

Reference for QA engineers who drive the WinForms UI and assert on rows in XTTDB. **Source snapshot:** the `C:\Dev\Exatouch` tree as of 7 September 2026 (the app folders are not git-managed; the built binary `2.0.x\bin\Exatouch.exe` is dated 5 June 2026). Line numbers are valid for that snapshot only; re-anchor after the next merge. This document lives in the `qa-ai` repo (commit `020c6cacb76b` at the time of writing). Everything here comes from the code-reading slices (SaveOrder, btnProcessClick A/B, tender staging, totals, the other register handlers, frmRefund, csPayment, Managers/Dapper, satellite writers, DB plumbing), the live schema dump (`db-schema.txt`) and the stored-procedure dump (`db-procs.txt`). Where two slices disagreed the code was opened and the line that settles it is cited. Nothing under `C:\Dev\Exatouch` is ever modified by a test.

**Conventions.** Paths are under `C:\Dev\Exatouch\2.0.x\`; a bare line number is `Transactions\frmRegister.cs`. Short names: `frmRefund.cs` = `Transactions\frmRefund.cs`, `csPayment.cs` = `Library\csPayment.cs`, `Lib.cs` = `Library\Lib.cs`, `OrderManager.cs` = `Managers\OrderManager.cs`, `frmGetOrders.cs` = `Transactions\SubForms\frmGetOrders.cs`, `frmReView.cs`/`frmComplete.cs`/`frmOfflineProcess.cs` = `Transactions\SubForms\`, `frmDelManager.cs` = `Transactions\Delivery\frmDelManager.cs`, `csObject.cs` = `Controls\csObject.cs`, `csDualPriceManager.cs` = `Library\csDualPriceManager.cs`, `GenericRepository.cs` = `Exatouch.Repositories\Common\GenericRepository.cs`. Enum ints are written inline (`Completed=1`); the full tables are in section 2. "raw" = `SqlCommand.ExecuteNonQuery/ExecuteScalar/ExecuteReader` (a `SqlException` propagates out of the click handler); "wrapper" = the `ExecNonQuery/ExecScalar/ExecReader` extensions (Lib.cs:3517-3640; modal error dialog, then `null` is returned and the caller continues). "batch" = several statements in one `CommandText`: one round trip, **not** one transaction.

**Build caveat that changes expectations.** The checked-in `Exatouch.csproj` has a single configuration with `DefineConstants=DEBUG;CFD` (csproj:30-38, every solution configuration maps to it, Exatouch.sln:27-35). In that binary: the card processor is faked (`_OnProcess` fabricates PAN, `CardHolder='Fake credit card'`, `ExpDate='0130'`, 6-char GUID `AuthCode`, random 5-digit `TransmitID`, decline when `Millisecond%33==0`, 90 % partial approval when `%27==0`, csPayment.cs:1447-1495; `TxRespons` is written NULL, csPayment.cs:87-91). Two consequences the fake branch adds (csPayment.cs:1465-1488): it sits at the top of `_OnProcess` with no `TxType` guard, so **voids, refunds (Return), captures and complete-auth also fail 1 in 33 times** with `ResponseMessage='Card Declined.'` — exactly the non-blank message that makes frmRefund.cs:2255 throw and `OrderManager.VoidOrder` roll back; and when `Account.TenderFee.Surcharge` is on every Credit approval fabricates `Surcharge=Round(AuthAmount×0.03,2)`, so `CCTxs.Surcharge`, `Payments.Surcharge`, `Orders.Surcharge` and `Orders.Total` all shift by 3 % on credit sales and the Tab Complete surcharge branch (17061) runs; `SecurityManager.Login` returns true unconditionally, so there are no manager-PIN gates and no `Log` "Manager PIN Request" rows (Managers\SecurityManager.cs:16-17); `Global.MgrID = Global.UserID = 1` at start (Main\frmMain.cs:2267-2269), so `OrdersLog` "Manager" rows carry `ID=1` instead of NULL; `Lib.OpenCashDrawer` is a no-op (Lib.cs:5772); Batch Close always runs the Training SQL (csPayment.cs:159-198). A binary built with an external `/p:DefineConstants` override behaves differently at every processor-dependent point (open question 1).

## 1. Big picture

### 1.1 Two persistence styles

**Legacy (dominant).** Forms build `SqlCommand` text by hand and execute it on the single shared `Global.SqlConnection` (csGlobal.cs:296, getter 309-382). The getter builds the connection lazily from `lpc:<MachName>\SQLEXPRESS`, catalog `XTTDB` (or `XTTDBTrain` for Training machines), Integrated Security, `MultipleActiveResultSets=True` (csGlobal.cs:254-259), and on **every** access pings `SELECT 1` with a 15 s timeout, looping `Close()/Open()` behind modal dialogs when that fails (340-376). A tree-wide grep for `BeginTransaction|TransactionScope|SqlTransaction|BEGIN TRAN|XACT_ABORT` finds exactly one hit, `Data\XTUnitOfWork.cs:43`, on a different connection. **Every legacy statement therefore autocommits.** Multi-statement CommandTexts (dozens of them, e.g. the per-tender commit 17938-17963 and the completion batch 18622-18644) give ordering and one round trip; each statement inside still commits alone and `XACT_ABORT` is never set, so a failure in statement N leaves 1..N-1 committed and, for a statement-level error, still runs N+1.

**New (narrow).** Static `Managers\*` classes open an `XTUnitOfWork` and write through Dapper `GenericRepository<T,K>`: `Add` emits `INSERT INTO <table> (<all non-key columns>) VALUES (...); SELECT SCOPE_IDENTITY();` (GenericRepository.cs:33-35); `Update` is a **full-row** `UPDATE <table> SET <every non-key property> WHERE key=@key` (115, 170-194) that rewrites every column with the value read moments earlier (columns that are not entity properties — `Orders.SplitPayments`, `Orders.Surcharge`, `CCTxs.Surcharge` — are never touched); `UpdateFields` is a partial SET (133-143); `Delete` throws NotImplemented (148-151). `new XTUnitOfWork(true)` opens its **own** `SqlConnection` and calls `BeginTransaction()` (XTUnitOfWork.cs:39-43); `Dispose()` of an uncommitted unit rolls back (137-150). `new XTUnitOfWork()` without the flag has no transaction. Only `OrderManager.VoidOrder` (OrderManager.cs:64), `AccountPaymentManager.TakeAccountPayment` (AccountPaymentManager.cs:34) and the single-statement `AccountPaymentManager.UpdateAccountPaymentRate` (103-111) use the transactional form; `PaymentManager.UpdateTip`, `GiftCardManager.*`, `CardTransactionManager.UpdateNumber`, `OrderManager.RequestRemoteReprint` and `AuditManager` autocommit on their own connection. Because that connection is separate from `Global.SqlConnection`, a Dapper write inside the void transaction holds X-locks that block legacy readers of the same rows (`read_committed_snapshot` OFF; 30 s command timeout).

### 1.2 Error behaviour of one statement

- **Wrapper** calls (`ExecNonQuery/ExecScalar/ExecReader`, Lib.cs:3517-3640) catch `SqlException`, call `_SqlException` (Lib.cs:8925-8953) which is **modal**: "Error executing SQL Script … Retry?" — Retry does **not** re-execute (the `break` runs regardless, Lib.cs:3532-3536); Cancel writes `log.txt` and asks "Ignore?" — OK returns `null` to the caller, otherwise `Global.MainForm.Restart=true; Start.Exit()`. Callers rarely test the null: a null `OrderID`/`PayID`/`ODID` then throws `NullReferenceException` at the next line (2049, 2440, 2607, 2727) and a second "Please contact Exatouch support" modal appears (Lib.Error via Start.cs:233-253). An `InvalidOperationException` is the only case that re-executes the command: `_SqlConnException` (Lib.cs:8909-8923) rebinds the command to `Global.SqlConnection` and loops; when the connection is Closed/Broken each pass shows a modal "Connection to the server has been lost. Would you like to Retry?" (Cancel → "Ignore?" → null result, or `Start.Exit()`), and when the connection is still Open (e.g. a MARS/DataReader conflict) the loop is silent and unbounded.
- **Raw** calls throw. frmRegister.cs has 78 raw `ExecuteNonQuery` sites versus 8 wrapped; frmRefund.cs 16 versus 1. `SaveOrder` (1748-2817) and `btnProcessClick` (16277-19115) have no try/catch, so an exception aborts the handler mid-sequence with everything before it committed.
- The UI-automation consequence: any DB failure produces one or two modal dialogs that must be dismissed, and the residue is a prefix of the write list, never a rollback (section 11).

### 1.3 Schema properties that matter

XTTDB has **no foreign keys** (`sys.foreign_keys` empty), **no triggers** (`sys.triggers` empty) and **no check constraints** on order tables; only clustered PKs and plain nonclustered indexes (Orders: BatchID, CustomerID, StaffID, TerminalID; Payments: BankID, CCTxID, OrderID, TerminalID, UserID; OrdersDetails: CatID, ItemID, OrderID, ServiceID, StaffID, SubCatID, UserID; OrdersTips: OrderID, PayID, StaffID). Every non-key column is nullable with no default except `CDate` (`getdate()`) and `Active` (`1`) on Bank/FloorPlan/Staff. Orphans are possible and nothing cascades: `Payments.OrderID=-OrderID` (soft delete, 2564, 18996) is a normal state, `FloorPlan.OrderID` can point at a joined-away order (Lib.cs:5013 path), and a deleted floor-plan table sets `Orders.FPID=NULL` on all history (Integrations\FloorPlan\frmSetFloorMap.cs:620).

### 1.4 Identity values and how they flow back into the form

Keys come back three ways: `OUTPUT INSERTED.[col]` read with `ExecuteScalar` (first column, first row) — the frmRegister/frmRefund norm; `SELECT SCOPE_IDENTITY();` appended to a batch (split child 19619, refund order frmRefund.cs:2450, every Dapper `Add`); `@@IDENTITY` in the stored procedures (db-procs.txt:60, 65) and clock-in SQL (Lib.cs:770) — safe only because no trigger exists.

| Key | Produced by | Lands in | Used later for |
|---|---|---|---|
| `Orders.OrderID` | `INSERT INTO [Orders] … OUTPUT INSERTED.[OrderID]` 2030-2032, `MainID = mSqlCmd.ExecScalar()` 2045 | `frmRegister.MainID` (DBNull for a never-saved cart; `mIsNew = MainID.Equals(DBNull.Value)` 1750) | every later `WHERE [OrderID]=@OrderID`, the on-screen order number (2047-2049), `Clear()` lock release (739) |
| `OrdersDetails.ODID` | `INSERT INTO [OrdersDetails] … OUTPUT INSERTED.[ODID]` 2425-2440 | the `_OrderDetails` DataRow column `ODID` | insert-vs-update on the next save (2371-2374 vs 2455), `csDelRow(ODID, ItemID)` queued deletes (13542-13545), `OrdersTimeCharge.ODID` (2447) |
| `Payments.PayID` | `INSERT INTO [Payments] … OUTPUT INSERTED.[PayID]` 2595-2607 | the `_Payments` DataRow column `PayID` (DBNull while staged) | `CardData.PayID` (17124) so `csPayment.SaveTrx` can link `CCTxs` (csPayment.cs:108-118); every `WHERE [PayID]=@PayID` in the tender loop (17972); `Orders.TabAuthID` (17490); remove-vs-soft-delete (19362-19365, 2558-2569) |
| `CCTxs.CCTxID` | `INSERT INTO [CCTxs] … OUTPUT INSERTED.[CCTxID]` csPayment.cs:93-104 | `CardData.CCTxID`, then `Payments.CCTxID` via `UPDATE [Payments] SET [CCTxID]=@CCTxID, [Reference]=@AuthCode WHERE [PayID]=@PayID` (csPayment.cs:112-118, skipped when `PayID` is DBNull) — batch A (17938) then rewrites `Reference` from the staged row value, so a card row's `Reference` ends **NULL** after the pay flow (the AuthCode survives only in `CCTxs.AuthCode`; tab-auth rows return before batch A and keep it) | voids, captures, refunds join `Payments` to `CCTxs` on it |
| `OrdersTips.TipID` | `OUTPUT INSERTED.[TipID]` 2724-2727, 16794, 17554 | `_Tips` row / `mValues.Object4` | update/delete by TipID (2702, 2733), WithError negation (19040) |
| `OrdersTax.OTID` | `OUTPUT INSERTED.[OTID]` 2640-2644 | `_Taxes` list | FIFO reuse on re-save (2649-2654), surplus nulled (2671-2675) |

`Global.TerminalID` is the `Terminals` row for this MAC (`UPDATE [Terminals] SET [Name]=@Name OUTPUT INSERTED.[TerminalID] WHERE [MAC]=@MAC`, frmMain.cs:1927; insert when null, 1939-1943) and is bound to both `Orders.TerminalID` and `Orders.InUseID` on insert (2032-2034: the same `@InUseID` parameter feeds both columns) and to `Orders.InUseID` on recall (8922-8925). `Global.UserID` comes from `Lib.CheckPIN` (Lib.cs:1037); `Global.MgrID` is set by manager overrides and **never cleared** (Lib.cs:1072, 1133; default `DBNull.Value`, csGlobal.cs:170). `Global.LocalSetting.BankID` lives in `Exatouch.cfg`, not the DB, and is what every `Payments.BankID` binds (`?? DBNull.Value`: 2598, 16161, 17971, 18340; frmRefund.cs:1812, 2693, 2778; frmDelManager.cs:630).

### 1.5 Threads and the shared connection

Completion runs its close batch on a **new thread** (18620) over the shared MARS connection while the UI thread runs `Clear()` → `UPDATE [Orders] SET [InUseID]=NULL` (739). Assertions must therefore poll for `Orders.Status=1 AND CloseDate IS NOT NULL` rather than read immediately after the click. `AuditManager` (`Log`) always uses a second connection (AuditManager.cs:190). Printing runs on detached threads except `OrderDetailsAsync`, which despite its name blocks on `WaitOne` (Reports\csPrint.cs:3071-3085) so its `OrdersDetails.Printed/Fire` update completes before `SaveOrder` returns.

## 2. Data model cheat-sheet

Column lists are from `db-schema.txt` (Orders, OrdersDetails, Payments, CCTxs, OrdersTips, OrdersTax, OrdersDelivery, OrdersFPSplit, FloorPlan, GiftCards, Terminals, AccountPayments). Tables **not** in the dump (OrdersLog, Items, Bank/BankMod/BankDetails, CustomFields, Log, OpenPromoDetails/OrdersPromoDetails, CCTxFailures, Staff, Customers, Config) are described from the SQL text only (open question 2).

### 2.1 Orders (OrderID identity, CDate default getdate())

| Column | Written by / meaning |
|---|---|
| `Status` smallint | `OrderStatus` (2.4). Open=0 on insert (2030, @Status 1858); Pending=6 per tender (17979); Completed=1 at close (18651); never in the re-save SET list (2093) |
| `Paid` money | `IIF(@NoTender=1,NULL,0)` on insert (2032; `@NoTender=_Payments.DefaultView.Count==0` 1982); `+=Amount` per tender (17960); `+=Tip` in `csPayment.Complete` (csPayment.cs:502); `-=Amount` in `VoidAuth` when `pVoidOrder=false` (1138); `-=_Ref` for Pending refunds (frmRefund.cs:2725) |
| `Total`, `Tax`, `ItemTotal`, `BaseAmount`, `TDUsed`, `TFUsed`, `Surcharge`, `OtherAmount`, `DeliveryFee`, `Tip`, `Credit`, `Exchange`, `Loyalty` | form totals at every `SaveOrder` (2030/2093); `Total += tip` (16794, 17554, csPayment.cs:502, frmReView.cs:160); `Tip` rewritten to `_TipAmt` at close (18652); `Surcharge` = SUM(Payments.Surcharge) (17961, 18666, csPayment.cs:527) |
| `InUseID` int | lock: `Global.TerminalID` on insert (2034) and recall (8925); NULL in `Clear()`/form close (739, 2969), frmRefund `_Clear`/Cancel (frmRefund.cs:222, 1571); frmRefund load sets it to `Global.TerminalID` (725) |
| `TerminalID`, `StaffID`, `TaxID`, `CustomerID`, `CAID`, `CustomID`, `OtherID`, `FPID` | insert (2030); re-save rewrites all but `StaffID`/`TerminalID` (2093); `StaffID` changed by Reassign/Take (19234/19246, 14802/14808), join source (Lib.cs:5013), delivery driver (Lib.cs:6947) |
| `CloseDate` | close batch (18639), Pay In/Out (16135), grid void (frmGetOrders.cs:429), join source (Lib.cs:5013), refund/open-refund orders (frmRefund.cs:2448, 1808), delivery cash close (frmDelManager.cs:606); reset to NULL by offline decline (frmOfflineProcess.cs:200) |
| `BatchID` | literal `0` at close, NULL when a tax-code item exists (18647); NULL for delivery close; never the bank (LMP integration writes it for reporting, Integrations\LMP\csClassLMP.cs 879/1060/1185 — not traced) |
| `TabAuthID`, `TabLimit`, `TabMin`, `TabAlert`, `Label` | tab auth (17486-17501); `TabAuthID=NULL` on complete (17085) and on void of the tab row (csPayment.cs:1146) |
| `RefundAmt`, `VoidDate`, `RefID`, `ReasonID`, `Note` | `RefundAmt += _Ref` / `=Paid` on refund/void (frmRefund.cs:2706-2725, OrderManager.cs:168); `VoidDate` (frmRefund.cs:2725 void, OrderManager.cs:169); `RefID` = original order on refund orders (frmRefund.cs:2448), = target on join sources (Lib.cs:5013), = `CustomFields.CustomID` on Pay In/Out (16135); `ReasonID` grid void (frmGetOrders.cs:429), refund orders (frmRefund.cs:2448, 1808) |
| `Printed` | 0 on insert and every re-save (2032, 2113); 1 by `_PrintOrder` (9933) and the online-order timer (frmMain.cs:3321) |
| `Source`, `OrderType`, `LegalAge`, `LegalAgeMethod`, `LicenseName`, `PhoneNumber`, `SplitPayments`, `CustExempt` | insert (2030); `OrderType` re-save (2093) |
| `PaidAtTable` | only `spApplyPayment` (db-procs.txt:71) |
| `RequestRemoteReprint` | Dapper 1/0 (OrderManager.cs:37/243) |
| `Rate` | CFD star rating (SysForms\CFD\frmCFD.cs:632) |
| `OtherRefunded`, `ConvFee`, `OnlineOrder`, `OOTime`, `RemoteStatus`, `CashDiscEnabled`, `InUse`, `Sent`, `MachName` | read only in the traced code (`OtherRefunded` reader frmRefund.cs:323; writer unknown, open question) |

### 2.2 OrdersDetails (ODID identity, CDate default)

Line rows. `OrderID`, `ItemID` (reserved pseudo-items: Refund=1, Certificate=5, GiftCard=6, Club=7, CommentPrice=8, ShareItem=9, Comment=10, ShareService=11, Course=14, Seat=15, NotPrinted=19), `Qty`, `QtyDel` (cumulative removed qty), `RefQty` (cumulative refunded qty), `Deleted` (soft delete), `DelUserID`, `UserID` (creator, `Global.UserID` 2387), `StaffID` (server), `LinePos` (grid index at save, 2342), `Price` (net of included tax), `PriceBase`/`PriceDisplay`/`PriceOriginal` (list price), `PriceSell` (net of every discount, 11865), `PriceTax`/`PriceSinTax` (per-unit tax), `Printed` (**money**: `=Qty` when remote-printed, csPrint.cs:1922; `=-Qty` on soft delete, 2530), `Fire` (bit), `Modifier`/`ModHas`/`ModRollup`, `PromotionID`/`DiscType`/`DiscUnit`, `PromoCode`/`PromoPrice`/`PromoGrpID`/`PromoLink`/`PromoQty`/`PromoUnit`/`PromoUnitDisc`, `SDID`/`LoyaltyUnit`/`LoyaltyDisc`/`ScanData`, `TaxableA-D`/`TaxExempt`/`TaxType`/`TaxRatio`/`TaxIncluded`, `ToGo`, `Shared` (split share rows), `SKU`/`ScannedSKU`, `Unit` (HR/MN/SC → OrdersTimeCharge), `DepartmentID`. There is no `Exchange` column: exchange lines are `Qty<0 AND Ratio IS NULL` (8691).

### 2.3 Payments, CCTxs, OrdersTips, OrdersLog, OrdersTax, OrdersDelivery, OrdersFPSplit, FloorPlan, Bank, Items

**Payments** (PayID identity): `OrderID` (negative = soft-deleted, 2564/18996), `Type` = `PayType`, `Status` = `PayStatus`, `Amount` (applied), `Value` (cash tendered / gift remaining balance / tab authorized amount / loyalty points), `Tip` (share of the order tip allocated to this tender, 17994-18017), `TDUsed`/`TFUsed`/`Surcharge`/`FeeFromTips` (tender discount/fee/surcharge), `DeliveryFee` (driver share), `CCTxID`, `Reference` (check number, pay-out note; for card rows `SaveTrx` writes the AuthCode but batch A overwrites it with the staged value, NULL — only tab-auth rows keep it), `RefID` (coupon PromotionID, pay-out reason CustomID), `RefundAmt`, `BankID`, `TerminalID`, `UserID`, `CloseUserID`, `CloseDate`, `MachName` (only `'Clover Flex'` from `spApplyPayment`), `FromSplit` (never written in traced code).

**CCTxs** (CCTxID identity): `TerminalID`, `TID`, `TransmitID`, `AType` (AccountType), `AuthCode`, `PNRef`, `CardType`, `CardHolder`, `CCNumber` (8 chars `'****dddd'`), `ExpDate`, `Number` (varbinary, RSA-encrypted PAN, offline/eGift only), `Sign` (image), `Source` (MANUAL=0, SWIPE=1, EMV=6), `Status` = `CCStatus`, `TxType`, `Tip`, `Surcharge`, `Partial`, `RAmt` (voided/refunded amount), `OffLineStatus`, `BatchClose`, `CloseDate`, `TxRespons`, `BatchID` (never written by 2.0.x). **There is no amount column**; the amount lives on `Payments`.

**OrdersTips** (TipID): `OrderID`, `PayID` (NULL for order-level Group/Amount/Percent tips), `PayOutID`, `StaffID`, `Amount`, `Value` (percent fraction or amount), `Type` = `TipType`, `RefAmount` (set on refund, frmRefund.cs:2530).

**OrdersLog** (OLID identity, CDate default; not in the dump): `OrderID`, `ID` = a `Staff.StaffID` (reports join on it, Reports\csSQL.cs:43, 860), `Type` = `OrderLogType`. Only INSERTed, never updated or deleted.

**OrdersTax** (OTID): one row per tax slot `Type` 0..29 with `Qty` (taxable basis) and `QtyExempt`; Types 0-3 always exist for a saved order (2626-2644); surplus rows are nulled (`Qty/QtyExempt/Type=NULL`), never deleted (2671). **OrdersDelivery** (ODELID): `3PDSID`, `OrderID`, `StaffID` (driver), `Status` = `DeliveryStatus`, `Ready/Out/Delivered` timestamps, `DeliveryFee`, `DeliveryFeeDollar` (bit), `DeliveryFeeTotal` (**bit** — SaveOrder binds a money amount, stored as 1 when non-zero, 2194), `RefNumber`. **OrdersFPSplit** (FPSID): `FPID`, `OrderID`, `Status` (FPStatus) — one row per sub-order sharing a table. **FloorPlan** (FPID): `OrderID`, `InUse`, `Status` = `FPStatus`, `LastAct` (stamped `GETDATE()` when `InUse` is written 0, csObject.cs:469-479), `Label`, `Active`. **GiftCards** (GiftID): `GiftCode`, `Amt`, `Balance`, `Used`, `UsedDate`, `DateSold`, `FromRefund`, `CustomerID` (0, not NULL, for walk-ins created from the register, Lib.cs:7351-7360), `OrderID`. **Bank** (`Status`: Open=0, SwapIn=1, SwapOut=2, Closed=3; `StaffID`, `MngrID`, `TerminalID`, `ShiftBegin/End`, `CashOpen`, `Delta`, `PayOutIn`, `RefCash`), **BankMod** (`BankID`, `UserID`, `MngrID`, `Cash` signed, `Date`, `Notes`), **BankDetails** (`BankID`, `Type`=PayType, `Amount` counted, `Expected`). **Items** stock: `QtyOnHand`, `QtySold`, `StockType` (Regular=0, NotInventory=2), `AutoMoveBulk`, `Alert`, `Active`. **Terminals**: `TerminalID`, `MAC`, `Name`, `AuthDate`/`Sent` (first online credit approval), `OffDate`/`OffSent` (offline transactions pending).

### 2.4 Enum values (ints as stored)

| Enum (source) | Values |
|---|---|
| `OrderStatus` (Library\csEnums.cs:854) → `Orders.Status` | None=-1, Open=0, Completed=1, JoinIn=2, Deleted=3, Refunded=4, Voided=5, Pending=6, RefPending=7, VoidPending=8, RefProcessing=9, OpenRefund=10, Undeliverable=11. `dbo.OrderStatus()` maps only -1..7 (db-procs.txt:4). No writer of 3, 7, 8, 9 was found in the traced code |
| `PayStatus` short (csEnums.cs:912) → `Payments.Status` | Open=0, Completed=1 ('Paid'), RemoteQueue=2, RemoteProgress=3, RemoteError=5, Auth=6, Refunded=8, Voided=9, TabAuth=11, OpenRefunded=13, Deleted=14. `dbo.PayStatus()` also names 4 Pending, 7 Partial, 10 Ref Pending, 12 Void Pending (db-procs.txt:8), which the C# enum lacks |
| `PayType` short (Exatouch.Entities\Enums\PayType.cs) → `Payments.Type` | NoTender=-1, Cash=0, Check=1, Coupon=2, Credit=3, Debit=4, Discount=5, EBTFood=6, eGift=7, PayIn=8, PayOut=9, Reward=10, Offline=11, Certificate=12, PayOutCust=13, CheckCashBack=14, ClubDiscount=15, DeliveryService=16, Factor4Loyalty=17, EBTCash=18, OnAccount=19, CardOnFile=20 (stored as 3, 2583). `dbo.PayType()` stops at 19 |
| `OrderType` (csEnums.cs:871) → `Orders.OrderType` | Regular=0, TakeOut=1, PickUp=2, Delivery=3, Custom=4, DeliveryService=5, PayIn=6, PayOut=7 |
| `CCStatus` short (csEnums.cs:537) → `CCTxs.Status` | Authorized=0, Completed=1, TabAuth=2, Voided=3 (`spApplyPayment` also writes 6, not a member) |
| `TxType` (XTPE2, `dbo.CCTxType` db-procs.txt:2) → `CCTxs.TxType` | Auth=0, Capture=1, Sale=2, Force=3, Adjustment=4, Return=5, Void=6, Inquiry=7 |
| `AccountType` (XTPE2\Enums.cs) → `CCTxs.AType` | Debit=0, Credit=2, EBTCash=3, GiftCard=4, EBTFood=5, Offline=6, CardOnFile=7 (Lib.cs:9073) |
| `OffLineStatus` (csEnums.cs:445) → `CCTxs.OffLineStatus` | None=0, Failed=1, Declined=2, Approved=3, Voided=4 |
| `OrderLogType` (csEnums.cs:834-843) → `OrdersLog.Type` | Create=0, Close=1, Manager=2, PrevOwner=3, Force=4, ApprovedBy=5, Voided=6 |
| `TipType` short → `OrdersTips.Type` | None=0, CC=1, Group=2, Amount=3, Percent=4, Terminal=5, Gift=6, CFD=7 |
| `FPStatus` (csEnums.cs:650) → `FloorPlan.Status`, `OrdersFPSplit.Status`, socket codes | None=0, Open=1, Busy=2, Ordered=3, Check=4, Authorized=5, CleanUp=6, TabAuth=7, Pending=8, Error=9, Split=10; popup commands Complete=50, Fire=51, Join=52, Move=53, Reassign=54, Take=55, TipAdd=56, TipDel=57, Print=59, FullCash=60, FullCredit=61, FullDebit=62 |
| `DeliveryStatus` short (csEnums.cs:563) → `OrdersDelivery.Status` | New=0, Ready=1, Out=2, Delivered=3, Undeliverable=4; `StaffDeliveryStatus` (csEnums.cs:1063) Available=0, Assigned=1, Out=2 |
| `DiscType` (csEnums.cs:591) → `OrdersDetails.DiscType` | TenderDiscount=1, Club=2, DollarAmount=3, Global=4, MfgGlobal=5, MfgSingle=6, Override=7, Single=8, Promo=9, Reward=10, ModCredit=11, ScanData=12 |
| `StockType` (csEnums.cs:465) → `Items.StockType` | Regular=0, NotInventory=2 |
| `MachineType` (csEnums.cs:348-358) | StandAlone=0, Server=1, Sidekick=2, Training=3 (Training uses `XTTDBTrain`) |
| `CustomType` → `CustomFields.Type` | RefundReason=14, PayIn=16, PayOut=17, PayOutCustomer=18; default reason ids: 39 refund, 40 void, 43 pay-out customer |

## 3. Creating a new order: `SaveOrder` step by step

`SaveOrder(object sender)` (1748-2816) is the **only** routine that persists a Register order — for a new cart it INSERTs, for a recalled one it UPDATEs, and in both cases it walks lines, payments, taxes, promos and tips. Nothing else on the Register creates an `Orders` row except Pay In/Out (16135), Split children (19615) and the refund screen (frmRefund.cs:1808, 2448, 2961).

### 3.1 Who calls it (the `sender` matters)

| UI action | Call site | `sender` | Around the save |
|---|---|---|---|
| **Save** (hold), Print Order, Print Remote, To-Go Print | btnSaveClick 19399 | the button | Runs only when `Modified \|\| (RemoteOption.Firing && (_CmdFire.Selected \|\| sender==_CmdPrintRemote))` (19392-19396); Save with nothing changed → "Nothing changed." (19417). On success `_DefaultScreen(1)` → `Clear()` → `UPDATE [Orders] SET [InUseID]=NULL` (19407 → 6922-6923 → 739-743). Result: a held ticket is simply `Status=0, InUseID=NULL, Paid=NULL` — **there is no "held" status** |
| Add Tip | btnAddTipClick 13168 → btnSaveClick | `_CmdAddTip` | no `Clear()`; TipSlip printed (19438-19439); lock stays |
| **Pay / Process** | btnProcessClick 16637 | tender button (`_TndCash1`, `_TndCredit1`, …) | `if (Modified)`; the order **and its staged tender rows** are persisted **before** the processor is called; also 16371 (`_CmdSave`, tab-auth first record), 16530 (`_TndTabComplete1`, tab owned by another terminal, then `Clear()` 16543 and exit), 17423 (partial-approval cancel with TD/TF) |
| Leaving / recalling | `_CheckForSave` 6397, 6411 | `_CmdSave` | AutoSave → save + `Clear()`; else "Save Changes?" — frmMessageBox maps visible **Yes**→`DialogResult.OK` (save + `Clear()`), **No**→`Cancel` (continue **without** saving), **Cancel**/X→`No` (abort) (SysForms\frmMessageBox.cs:130-143) |
| Fire Now | btnFireNowClick 13972 | the button | `_CmdFire.Selected=true` first; `Clear()` right after (13973) |
| Recall with Join | btnGetOrderClick 14054 | Get-Order button | only when frmGetOrders raised `OnJoinOrder`; then `UPDATE [Orders] SET [Tax],[Total]` (14059-14066) |
| Group tip apply/remove | btnPercentClick 16267 | `_CmdPercent` | `_Tips` row `Type=Group=2`, `Value=GroupTip/100` (16243-16249) or `Deleted`; saves only an existing order (16265) |
| Split | btnSplitClick 19597 | `_CmdSplit` | saves the source before the child INSERT (19615) |
| Appointment checkout | CheckOut 714 | boxed `true` | then `UPDATE [Appointments] SET [OrderID],[Completed]=1` per appointment (717-727) |

**Why Pay always saves a recalled order.** reg-process-a read `Modified` as never set by a tender click; reg-save said the grid sets it. The code settles it: `_AddPayment` adds the staged row to `_Payments` (4595-4602); `dgvRC.DataSource=_Payments` (1640); `csDataGridView.OnRowsAdded` sets `FormParent.Modified=true` when `IsHandleCreated && AllowUpdate && FormParent!=null && !IsNewRow` (Controls\csDataGridView.cs:927-933); `AllowUpdate` defaults to true (csDataGridView.cs:82) and `csForm` assigns `FormParent=this` to every `csDataGridView` in its tree (Controls\csForm.cs:824); the `Modified` setter is a plain flag guarded by `Global.IsRunTime` and `!_Paused` (csForm.cs:104-137). `dgvRC` sits on `tpgPayments` (frmRegister.Designer.cs:2793), so its handle exists once the Payments tab has been shown — i.e. on every normal tender click. Residual caveat: a pay path that never shows that tab in the session (floor-plan popup FullCash=60/FullCredit=61/FullDebit=62) could leave `IsHandleCreated` false; verify empirically before asserting on PayID-keyed rows there.

### 3.2 Validation (1755-1913) and the three writes hidden in it

Preconditions: detail rows or a paid/PayOutCust amount (1757-1767); `_Total >= _Paid` (1769-1778); `_Total <= 99,999,999.99` (1780-1784); label prompt when `Register.Order.LabelOnOrder` and no label/customer (1787-1808; Cancel → `_ResetPay()` and `return false`); in-house Delivery minimum-order override (1810-1824) **else-if** `Register.Order.ReqCustomer` customer lookup (1825-1830); for an existing order `SELECT [InUseID] FROM [Orders]` must equal `Global.TerminalID` (1846-1854) else "Order may have been modified on another terminal, cannot save."; customer credit-limit check over the customer's `Status=0` orders (1860-1887) with a possible manager login; Release builds refuse after the license/trial date (1906-1910); DEMO builds reset all transactions at 100 orders (1889-1904, `Lib.ResetTrxs` Lib.cs:6345-6444 — TRUNCATEs every order table).

| # | file:line | table | write | condition |
|---|---|---|---|---|
| V1 | 1773 → 739 | Orders | `InUseID=NULL` then `return false` | `Global.IsLock && _Total>=0 && _Total<_Paid`, existing order |
| V2 | 1827 → 7785 | Customers | `ClubNumber=@ClubNumber` | ReqCustomer lookup picks a loyalty member without club number |
| V3 | 1878 → SecurityManager.cs:110 → AuditManager.cs:29 | Log | "Manager PIN Request" row (Dapper, second connection, written **before** the PIN dialog, even if cancelled) | credit limit exceeded, Release build only |

### 3.3 Flags and header parameters

`mIsNew = MainID.Equals(DBNull.Value)`; `mHasTabAuth = _HasTabAuth || (TabEnabled && sender==_TndTabAuth1)` (1750-1753). If `_HasTabAuth && Modified`, the tab row's `Amount=_GetBalance(PayType.Credit)` and `TFUsed` are recomputed in memory (1925-1954) and persisted by the Payments UPDATE (2613). Every `Orders` column is bound from form state (1960-2024); `PhoneNumber` = digits of `Customers.Phone` (2003-2005; a missing customer row throws before any write) or of `_PhoneNumber` for ScanData loyalty (2018-2021).

### 3.4 New order: the INSERT (2030-2045)

`INSERT INTO [Orders] (...) OUTPUT INSERTED.[OrderID] VALUES (...)`, wrapper `ExecScalar` → `MainID` (2045; null → NRE at 2049).

| Column | Value | Source |
|---|---|---|
| `CAID`, `CustomerID`, `CustomID`, `OtherID` | `_CAID`, `_CustomerID`, `_OTSelected.ID`, `_Other.PromotionID` | 1960, 1857, 1969; `CustomID` is expected NULL for built-in order types (open question) |
| `FPID` | `_CurrentObj.ID` only when `License.FloorPlanEnabled` | 1961 |
| `InUseID`, `TerminalID` | **both** `@InUseID` = `Global.TerminalID` | 2032, 2034 |
| `StaffID` | the form `StaffID` field = `Global.UserID` after `Clear()` (953) or `Orders.StaffID` of a recalled order (8589); changed by Reassign/Take (19255) | 1963 |
| `TaxID` | `Global.Tax.TaxID` | 1964 |
| `BaseAmount`, `Credit`, `CustExempt`, `Exchange`, `ItemTotal`, `Loyalty`, `Tax`, `Tip`, `Total`, `TDUsed`, `TFUsed`, `SplitPayments` | `_BaseAmount`, `_Credit`, …, `_TipAmt`, `_Total` (section 4 formulas) | 1971-1999 |
| `DeliveryFee` | `@DeliveryFeeTotal` = `_DeliveryFee` | 1971 |
| `Surcharge` | `_Surcharge`, **NULL when 0** | 1999 |
| `Label` | `_Label` unless it equals the phone digits → NULL | 2036-2043 |
| `LegalAge`, `LegalAgeMethod`, `LicenseName` | age-check state (Override=1/Manual=3/Scanned=4) | 1975-1977 |
| `Note` | `txtDiscNotes.Text`, NULL when blank | 1980 |
| `OrderType` | `(OrderType)_OTSelected.Tag` | 1984 |
| `OtherAmount` | `_Other.Ratio` | 1986 |
| `Paid` | `IIF(@NoTender=1, NULL, 0)`; `@NoTender = _Payments.DefaultView.Count==0` (view filter `Deleted=0 AND Status IN (0,1,6)`, 1638) | 1982, 2032 |
| `Printed`, `Source` | literal 0, literal 0 (`OrderSource.Exatouch`) | 2032 |
| `Status` | `@Status` = `OrderStatus.Open=0` | 1858 |
| `TabAlert` | `_TabAlert` (0 after `Clear()` 960) | 1992 |
| `PhoneNumber` | as above | 2001-2024 |
| defaults / NULL | `CDate=getdate()`; `BatchID, CloseDate, RefID, ReasonID, TabAuthID, TabLimit, TabMin, RefundAmt, VoidDate, PaidAtTable, RequestRemoteReprint, Rate, MachName` NULL | schema |

Right after: `INSERT INTO [OrdersLog] ([OrderID],[ID],[Type]) VALUES (MainID, @StaffID, 0)` (2052-2058, raw; `ID` = the `StaffID` field, `Type=OrderLogType.Create=0`); `(MainID, _AgeApprover, ApprovedBy=5)` when an age approval was recorded (2060-2068); with floor plan licensed and a table selected, `_CurrentObj.Status=FPStatus.Ordered=3; DBUpdate()` → `UPDATE [FloorPlan] SET [OrderID]=MainID, [InUse]=0, [Status]=3, [LastAct]=GETDATE() WHERE [FPID]` (2070-2084 → csObject.cs:421-423, 472). The status setter early-returns when unchanged or `(!EditMode && !ForUse)` (csObject.cs:412); then `DBUpdate` rewrites the **old** status and `InUse` (open question).

### 3.5 Existing order: the UPDATE (2087-2155)

One CommandText `DELETE FROM [OpenPromoDetails] WHERE [OrderID]=@OrderID; UPDATE [Orders] SET … OUTPUT DELETED.[OrderType], DELETED.[Total] WHERE [OrderID]=@OrderID` executed with wrapper `ExecReader(SingleRow)` (2089-2132; a null reader → NRE at 2133 after the DELETE committed). SET list: `CAID, CustomerID, CustomID, FPID, OtherID, TaxID, BaseAmount, Credit, CustExempt, DeliveryFee, Exchange, ItemTotal, Label, LegalAge, Loyalty, Note, OrderType, OtherAmount, Paid=IIF(@NoTender=1,NULL,[Paid]), Printed=0, TDUsed, TFUsed, TabAlert, Tax, Tip, Total, PhoneNumber, SplitPayments, Surcharge`. **Not touched:** `StaffID, InUseID, TerminalID, Status, LegalAgeMethod, LicenseName, Source`. Because `FPID` **is** in the SET list (settles reg-other-b's open question 1), a Save after a floor-plan Move rewrites `Orders.FPID` from whatever `_CurrentObj` the register holds. The ApprovedBy block for existing orders (2138-2145) is dead code (CommandText overwritten at 2149/2173, and it binds `@StaffID` not `@Approver`). With scheduling licensed and the pre-tip total changed: `UPDATE [Appointments] SET [CheckOutChanged]=1` (2147-2153).

### 3.6 Order-type change and delivery rows (2157-2220)

`mChangeType = !mOrderType.Equals(mOldType)` is **true for every new order** (`mOldType` null). It calls `OrderManager.RequestRemoteReprint` (2168; Dapper `Orders.RequestRemoteReprint=1` only when an `OrdersDetails.Printed` equals exactly 1 — never for a new order, OrderManager.cs:27-37). Type ∈ {Delivery=3, DeliveryService=5, PickUp=2} → `INSERT INTO [OrdersDelivery] (3PDSID, OrderID, DeliveryFee, DeliveryFeeDollar, DeliveryFeeTotal, Status=DeliveryStatus.New=0)` (2184-2195) or UPDATE when a row exists (2187); type moved away from those → `DELETE FROM [OrdersDelivery] WHERE [ODELID]` (2200-2203); unchanged Delivery/DeliveryService (not PickUp) → fees rewritten on every save (2206-2219).

### 3.7 Fire flags (2222-2319)

Under `mPrintAllow` (has rows, not GoPrintOrder/Split/Percent) `mPrintFire` is true for every new order, Fire Now or To-Go print. With `RemoteOption.Firing`, `Fire=true` is set in memory on the next unprinted lines and, because the detail loop binds `@Fire=mRow["Fire"]` (2341), **persisted** as `OrdersDetails.Fire=1`.

### 3.8 Detail lines (2321-2498): insert, update, delete, stock

`Parameters.Clear()` at 2326 discards the header parameters; every later block re-adds `@OrderID`. Rules per `_OrderDetails` row:

- **`ODID` NULL and not `Deleted`** → `INSERT INTO [OrdersDetails] (…) OUTPUT INSERTED.[ODID]` (2425-2440, wrapper) with every column of the DataRow (2328-2419) plus `OrderID=MainID` (2380), `CDate` = row CDate or `mNow` captured at 2322 (2385), `UserID=Global.UserID` (2387), `LinePos=i` (2342), `Fire` (2341), `DepartmentID` only under `License.DepartmentalReporting` (2420-2438). `DelUserID, Printed, QtyDel, RefQty` are not in the list → NULL; `Fire` is bound from the row, which `_AddRow` initialises to DBNull (626-628), so it is **NULL** unless `RemoteOption.Firing` set it to 1 (2264) — it becomes 0 only when a remote/KDS ticket prints (csPrint.cs:1921-1924). Units HR/MN/SC add `INSERT INTO [OrdersTimeCharge] (ODID, OrderID, StartTime=DATEADD(...,-Qty,GETDATE()))` (2442-2450).
- **`ODID` set** → full rewrite of 34 columns (2455-2468, raw): `DelUserID, ItemID, InstructID, PromotionID, StaffID, Deleted, Description, DiscType, DiscUnit, Fire, LinePos=i, LoyaltyDisc, LoyaltyUnit, ModAllowDisc, ModAllowPromo, ModHas, Modifier, Price, PriceBase, PriceDisplay, PriceSell, Printed=(mChangeType && Printed<>1) ? 0 : Printed (2465), PromoUnit, PromoUnitDisc, PromoLink, PromoQty, Qty, QtyDel, PriceSinTax, PriceTax, Shared, TaxExempt, ToGo, ScannedSKU` — changed or not.
- **Stock** — `if (!mRow["Qty"].Equals(mRow["Qty_Old"]))` (2473): `UPDATE [Items] SET [QtyOnHand]=ISNULL([QtyOnHand],0)+@Qty_Old-@Qty, [QtySold]=ISNULL([QtySold],0)+@Qty-@Qty_Old WHERE [ItemID]=@ItemID AND [StockType]=0` (2479-2485, raw; `Qty_Old` DBNull→0 for never-saved rows), then `mRow["Qty_Old"]=Qty` (2486); restaurants also `UPDATE [FreshSheet] SET [Qty]=max(0, …)` (2490-2495). This settles reg-totals' wording: the sale decrements stock **at SaveOrder** (first Save, Send, Print or tender click), not at completion; modifiers are included ("TODO Modifier can go negative", 2478).
- **Removed lines** — each `csDelRow` queued in `_Delete` (btnDeleteClick 13545/13569/13582, `_ModifyQty` 9131/9141, 6777, 9560): `ItemID` Comment=10 / CommentPrice=8 → hard `DELETE FROM [OrdersDetails] WHERE [ODID]` (2512); others optionally inherit the parent's `LinePos` (2521-2526) then soft-delete `UPDATE [OrdersDetails] SET [CDate]=GETDATE(), [DelUserID]=@UserID, [Deleted]=1, [Printed]=-[Qty], [QtyDel]=ISNULL([QtyDel],0)+[Qty] OUTPUT INSERTED.[Qty]` (2530-2535; `Qty` itself unchanged) and restore `UPDATE [Items] SET [QtyOnHand]=ISNULL([QtyOnHand],0)+@Qty, [QtySold]=[QtySold]-@Qty WHERE [ItemID] AND [StockType]=0` (2540-2545). Nothing reaches the DB until this save; `Clear()` empties `_Delete`, so "Remove line" then "Clear All" leaves the rows `Deleted=0`.

### 3.9 Payments (2554-2620)

The loop uses `_Payments.Select()` — **all** rows, ignoring the view filter. Rows `Deleted=true` with a `PayID` are soft-deleted: `UPDATE [Payments] SET [OrderID]=-[OrderID] WHERE [PayID]` (2564-2569, raw). Rows with `PayID` NULL are inserted (2595-2607, wrapper `ExecScalar` → `PayID` into the row):

| Column | Value |
|---|---|
| `BankID` | `Global.LocalSetting.BankID ?? NULL` (2598) |
| `CCTxID` | row value (NULL at this point; set later by `csPayment.SaveTrx`) |
| `OrderID`, `TerminalID`, `UserID` | `MainID`, `Global.TerminalID`, `Global.UserID` |
| `RefID`, `Reference`, `Amount`, `Value`, `TDUsed`, `TFUsed` | row values (coupon `PromotionID`; pay-out reason `CustomID`/43; check number; cash collected …) |
| `RefundAmt`, `Status` | literal `0`, literal `PayStatus.Open=0` |
| `Type` | row `Type`, with `CardOnFile=20` stored as `Credit=3` (2583) |
| `FeeFromTips` | `Tip × TenderFee.Amount` only when `DeductFeeFromTips` and the **original** type is Credit=3/Debit=4 (2585-2588); CardOnFile rows get NULL |
| NULL | `CloseUserID, CloseDate, Tip, Surcharge, MachName, DeliveryFee, FromSplit`; `CDate` default |

Rows with a `PayID` get `UPDATE [Payments] SET [Amount],[TDUsed],[TFUsed],[Type] WHERE [PayID]` (2613-2617, raw) — `Status`, `Value`, `Reference` untouched. `_LoadOrder` loads `Status<>9`, so `Deleted=14` rows are rewritten too.

### 3.10 Taxes, promos, tips, customer, audit (2622-2774)

- **OrdersTax**: 30 slots; slot `i<4` always, others when `Global.Tax.TaxValue[i]!=0 && (qty or exempt ≠ 0)`. `INSERT … (CDate=DateTime.Now, OrderID, Qty=_TaxQty[i], QtyExempt=_TaxQtyEx[i], Type=i) OUTPUT INSERTED.[OTID]` when no reusable OTID remains (2640-2644), else FIFO `UPDATE [OrdersTax] SET [Qty],[QtyExempt],[Type] WHERE [OTID]` (2649-2654); leftover OTIDs get `Qty=NULL, QtyExempt=NULL, Type=NULL` (2660-2677) — never deleted.
- **OpenPromoDetails**: re-inserted from `_OpenPromo` (`ODID, OrderID, Type=DiscType, Value`, 2683-2693); the existing-order path deleted them first (2090). At completion they are copied to `OrdersPromoDetails` and deleted (18629-18635).
- **OrdersTips** (only with `Register.Tip.Tips`): `DELETE FROM [OrdersTips] WHERE [TipID]` for `Deleted=1` rows with a TipID (2702-2708); `INSERT (OrderID, StaffID, Amount, Type, Value) OUTPUT INSERTED.[TipID]` for Type ∈ {Group=2, Amount=3, Percent=4} with TipID NULL (2724-2727; `PayID` NULL); `UPDATE [OrdersTips] SET [Amount],[Type],[Value] WHERE [TipID]` otherwise (2733-2737). CC=1/CFD=7/Terminal=5 tips are written by the pay flow, not here.
- **Customers**: `ClubNew`, `ClubNumber` when the club changed (2745-2752). **Log**: one row per `DataLog` entry (manager overrides on Register buttons; `RecID=MainID`, `Module=Transactions`, 2758-2773).

### 3.11 Print and reset (2776-2815)

`_RowModified` is overwritten with a "print a receipt" flag (2778). To-Go → `OrderDetailsAsync(PrintType.ToGo)` (2780); else receipt flag or `RemoteOnSave` → `RemoteAgain|ReceiptAndRemote|Remote|Receipt` (2783-2794); else `IncludeTab && _HasTabAuth` → `_PrintOrder` (2805; `UPDATE [Orders] SET [Printed]=1 OUTPUT INSERTED.[FPID],INSERTED.[TabAuthID]` 9933). Remote-type prints run `UPDATE [OrdersDetails] SET [Printed]=ISNULL([Qty],0), [Fire]=0 WHERE [ODID]` per line (csPrint.cs:1921-1932) **synchronously** (csPrint.cs:3083 `WaitOne`); `PrintType.Receipt=5` (guest check) never touches `OrdersDetails` (csPrint.cs:1892 guard). Finally `_CmdSave.Enabled=_RowModified=Modified=false` and `return true` (2808-2815). `SaveOrder` returns false only from validation; after the first INSERT/UPDATE the only other exit is an exception.

### 3.12 Complete write list of one `SaveOrder`, in order

| # | file:line | table | op | condition | exec |
|---|---|---|---|---|---|
| 1 | 2030 | Orders | INSERT (3.4) | `mIsNew` | wrapper |
| 2 | 2055 | OrdersLog | INSERT Create=0, `ID`=StaffID field | `mIsNew` | raw |
| 3 | 2063 | OrdersLog | INSERT ApprovedBy=5, `ID=_AgeApprover` | `mIsNew && _AgeApprover` set | raw |
| 4 | 2084 → csObject.cs:472 | FloorPlan | `OrderID=MainID, InUse=0, Status=3, LastAct=GETDATE()` | new order, floor plan, table selected | wrapper |
| 5+6 | 2090, 2093 | OpenPromoDetails; Orders | DELETE; UPDATE (3.5) | existing order | one CommandText, wrapper ExecReader |
| 7 | 2150 | Appointments | `CheckOutChanged=1` | existing, schedule licensed, pre-tip total changed | raw |
| 8 | 2168 → OrderManager.cs:37 | Orders | `RequestRemoteReprint=1` | type changed and a line `Printed=1` | Dapper |
| 9-12 | 2184 / 2187 / 2200 / 2209 | OrdersDelivery | INSERT / UPDATE / DELETE / fee UPDATE | 3.6 | raw |
| 13 | 2425 | OrdersDetails | INSERT per new line, OUTPUT ODID | `ODID` NULL, not Deleted | wrapper |
| 14 | 2447 | OrdersTimeCharge | INSERT | unit HR/MN/SC | raw |
| 15 | 2456 | OrdersDetails | 34-column UPDATE per saved line | `ODID` set | raw |
| 16 | 2480 | Items | stock move | `Qty != Qty_Old`, StockType 0 | raw |
| 17 | 2491 | FreshSheet | qty | inside 16, restaurant | wrapper |
| 18 | 2512 | OrdersDetails | hard DELETE | queued Comment/CommentPrice | raw |
| 19 | 2521 | OrdersDetails | `LinePos`=parent's | queued with parent (9560) | raw batch |
| 20 | 2530 | OrdersDetails | soft delete, OUTPUT Qty | queued non-comment | wrapper |
| 21 | 2540 | Items | stock restore | after 20, ItemID set | raw |
| 22 | 2564 | Payments | `OrderID=-OrderID` | `Deleted=true` with PayID | raw |
| 23 | 2595 | Payments | INSERT (3.9), OUTPUT PayID | `PayID` NULL | wrapper |
| 24 | 2613 | Payments | `Amount, TDUsed, TFUsed, Type` | `PayID` set | raw |
| 25-27 | 2640 / 2649 / 2671 | OrdersTax | INSERT / FIFO UPDATE / null-out | per slot | wrapper / raw / raw |
| 28 | 2683 | OpenPromoDetails | INSERT per `_OpenPromo` | `_OpenPromo.Count!=0` | raw |
| 29-31 | 2702 / 2724 / 2733 | OrdersTips | DELETE / INSERT / UPDATE | Tips enabled | raw / wrapper / raw |
| 32 | 2745 | Customers | `ClubNew, ClubNumber` | club changed | raw |
| 33 | 2758 | Log | INSERT per DataLog row | manager overrides | raw |
| 34 | 2780/2783 → csPrint.cs:1922 | OrdersDetails | `Printed=Qty, Fire=0` | remote-type print | wrapper, synchronous |
| 35 | csPrint.cs:2899 → OrderManager.cs:243 | Orders | `RequestRemoteReprint=0` | flag was 1 and a ticket printed | Dapper |
| 36 | 2805 → 9933 | Orders | `Printed=1` | `IncludeTab && _HasTabAuth`, no RemoteOnSave | wrapper |
| C1 | 19407 / 6399 / 6413 / 13973 / 16543 → 739 | Orders | `InUseID=NULL` | caller's `Clear()` right after a successful save | raw |
| C2 | 721 | Appointments | `OrderID=MainID, Completed=1` | CheckOut | raw |
| C3 | 14060 | Orders | `Tax=_Tax, Total=_Total` | recall-with-Join | raw |
| C4 | 18352 | Payments | INSERT `Status=1, Type=Cash=0, CloseDate=GETDATE()` | btnProcessClick's own insert for cash-back rows with PayID NULL (Type -1/1/13) | wrapper |

### 3.13 Save/hold vs Pay vs Complete — what the DB looks like after each

| Sender | Orders after the click | Payments | Lock |
|---|---|---|---|
| Save (`_CmdSave`) | `Status=0`, `Paid=NULL` (no tender) or unchanged, `Printed=0`, totals as computed; OrdersLog Create (new) | none new; staged Discount/partial rows are inserted with `Status=0` | `InUseID=NULL` (Clear) |
| Pay (tender button) | as Save, then per-tender `Paid+=Amount, Status=6`, then close batch `Status=1, CloseDate, BatchID=0, Tip=_TipAmt` (background) | inserted `Status=0`, then batch A `Status=1/6/8, CloseDate` | `InUseID=TerminalID` until the UI thread's `Clear()` after completion |
| Tab Auth (`_TndTabAuth1`) | as Save; then `TabAuthID=PayID, TerminalID, Label, TabMin, TabLimit`; `Status` stays 0 | `Status=11, Value=AuthAmount, CCTxID` | `InUseID=NULL` (17537 `_DefaultScreen`) |
| Tab Complete (`_TndTabComplete1`) | SaveOrder only if `Modified` (else the pre-calc writes 16572/16589/16619); capture; `TabAuthID=NULL`; then as Pay | tab row `Status=1` | as Pay |
| Complete window (`Complete(object)` 1061 → frmComplete → `_OnComplete`) | no `SaveOrder`; `csPayment.Complete` writes (section 8) | `Auth=6 → 1` | untouched |

## 4. Staging tenders and computing totals

### 4.1 The in-memory tables

`_OrderDetails` and `_Payments` are schema-only DataTables built by `Lib.TableFromDB` (`SELECT *, … FROM [Payments]` with `CommandBehavior.SchemaOnly`, 1632-1640; Lib.cs:7231-7244): every physical column exists on each DataRow plus helper columns that are never persisted (`_Payments`: `Deleted, Manual, PayStatus, PayType`; `_OrderDetails`: `Allow_Disc, Credit, Exchange, Ext, Promo_*, Qty_Old, Qty_Total, To_Process`, …, 1591-1612). `_Payments.DefaultView.RowFilter = [Deleted]=0 AND ([Status]=0 OR [Status]=1 OR [Status]=6)` (1638) drives the grid and `_PayUpdate`; `SaveOrder` ignores the view (3.9). Recalled orders fill `_Payments` with `SELECT *, dbo.PayType(Type), dbo.PayStatus(Status), 0 AS Deleted FROM [Payments] WHERE OrderID=@OrderID AND Status<>9` (8794-8800).

**Pressing a tender button writes nothing to order tables.** `btnPayClick` (15551-15918) resolves the amount — `frmCash` for Cash (`Amount<=balance && Collected>=Amount`, frmCash.cs:101-103; Exact Cash sets `mValue=mAmount=mBalance` 15653-15654), `frmPayOut`/`frmPayOutAmount` for pay-outs (`RefID=CustomID or 43`, 15686-15723), a CRM INVOICE for On Account (`AccountReceivableManager.CreateTransaction`, 15733 — a **remote** write at staging time), `frmMessageValue` for the rest — then `_AddPayment(type, balance, amount)` creates the row `Amount, Deleted=false, PayType=<name>, Status=PayStatus.Open=0, Type` (4595-4602) and `_UpdatePay` computes `TDUsed`/`TFUsed` (12583-12690: `TDUsed=Round(amount×TenderDisc.Amount[type],2)`; `TFUsed` from `_CalcTFUsed` capped at `_TFUsed`, using `_Payments.Compute("SUM([TFUsed])")` which includes `Deleted=true` rows, 12645). The row then carries `RefID, Reference, Value, Manual` (15896-15899) and `btnProcessClick(sender, EventArgs.Empty)` runs **synchronously** in the same click (15904). Specialised handlers stage `Discount=5` (15961-15969, `TDUsed=TFUsed=DBNull`), `EBTCash=18`/`EBTFood=6` (16017-16023 / 16068-16074, no processor balance inquiry), `Reward=10`/`Factor4Loyalty=17` (16234). Gift Card / Gift Certificate buttons are **item sales** (`_OrderDetails` line `ItemID=6`/`5`, `Qty=1`, all price columns = amount, 14117-14146 / 14185-14210), not tenders.

`_ResetPay` (19346-19387) removes Open rows with `PayID` DBNull and flags persisted ones `Deleted=true` (→ `OrderID=-OrderID` on the next save). `_RemovePayment` (4638-4651) physically removes a row without flagging it — used by `_CancelGiftCertificatePayment` (4653-4661) whose `Select("[PayType]='Certificate'")` also catches Completed certificate rows of a recalled order (open question).

Side-effect writes reachable from staging and ring-up (none touch order tables): `Customers.ClubNumber` "Missing membership number" prompt (7784-7788); `Customers` INSERT/UPDATE from the ScanData phone prompt via frmDelivery (Transactions\Delivery\frmDelivery.cs:342/350, audited through `AuditManager.WriteLog`); `Items.Active=1` for a scanned inactive item (5185-5189); JUUL re-categorisation `Categories/SubCat/Items` (5552-5626); Auto-Move-Bulk `Items/ItemsTx/ItemsTxDetails` (6702-6743, raw, throws); `Log` rows for manager-PIN prompts (Release only). The A/R "Take Payment" path writes `AccountPayments`/`AccountPaymentDetails` in a real transaction (AccountPaymentManager.cs:34-69) after the processor ran. Further ring-up side writes (item "86", e-mail receipt customer rows, `Customers.Distance`, delivery address rows) are listed in 9.11.

### 4.2 Formulas (`_TotalUpdate` 11409-12575, `_PayUpdate` 9654-9919)

| Quantity | Formula | Where |
|---|---|---|
| `OrdersDetails.Price` | `Lib.RemoveTax(row)` — list price stripped of included tax, else `PriceBase` | 5820, Lib.cs:6309-6343 |
| `OrdersDetails.PriceSell` | `Price − DiscUnit − PromoUnit − LoyaltyUnit`, then coupons, then rewritten as `(PriceSell×Qty − Discount-tender share)/Qty` — net of everything | 11602-11653, 11737-11767, 11865 |
| `Orders.BaseAmount` | `Round(Σ Price×Qty, 2, AwayFromZero)` | 11810, 12529 |
| `Orders.ItemTotal` | `Σ PriceSell×Qty` (**4 dp**, not rounded; neither the close batch nor 16794 rounds it — settles reg-totals' open question 4; the dual-price rewrite does, csDualPriceManager.cs:690) | 11851-11854 |
| `_SubTotal` | `Round(ItemTotal − TDUsed, 2)` | 12531 |
| `OrdersTax.Qty[i]` | taxable basis per slot; EBT-paid portions moved to `QtyExempt` | 11970-12346 |
| `Orders.Tax` | rebuilt from rounded buckets: `Σ Round(Round(_TaxQty[i],2)×TaxValue[i],2)` + tender-fee tax (all-TaxIncluded orders use a truncate/round trick) | 12459-12511 |
| `OrdersDetails.PriceTax` / `PriceSinTax` | `mTax/Qty` / `TaxType×TaxRatio×…` (0 when exempt) — `SUM(PriceTax×Qty)` may differ from `Orders.Tax` by a cent | 12404-12408, 12365-12390 |
| `Orders.OtherAmount` (`_OtherAmt`) | other coupon + `_TFUsed` + `_Surcharge` + `_DeliveryFee` | 12516-12527 |
| `Orders.Total` | `_SubTotal + _Tax + Round(_TipAmt,2) + _OtherAmt`; for a saved dual-price order paid with a DP tender `DualPriceOrderModel.Total + tip + other` (loader swallows exceptions and returns 0) | 12540, 12533-12538 |
| balance | `_Total − _TotalPay`; `_TotalPay` sums view rows **except** Discount=5, Coupon=2, ClubDiscount=15 (which never count as payment); `_Paid` sums only Completed/Auth/Refunded/Voided rows | 1210, 9705-9777 |
| `Orders.TDUsed/TFUsed/Surcharge` | Σ of the per-payment `TDUsed/TFUsed/Surcharge` over the view | 9709-9722 |
| EBT amount cap | EBT tenders are capped to the EBT-eligible item amounts ("Maximum EBT amount applied") | 1221-1291 |
| Coupon payment row | a Percent coupon's own `_Payments` row (`Type=2, Status=0, RefID=PromotionID`, created 411-418) gets `Amount=Round(mSubPromo,2)` on every `_TotalUpdate` — that is what `Payments.Amount` stores | 11770 |

`_TotalUpdate` is re-run on every change (about 73 call sites); everything in memory is recomputed, so assert only on what `SaveOrder` wrote. A `_TotalUpdate` that throws leaves the DataTables half-updated and the DB untouched.

## 5. Paying and completing: `btnProcessClick` (16277-19115)

### 5.1 Entry and preconditions

Every tender path ends in `btnProcessClick`: `btnPayClick` (15904), Exact Cash via `btnPayFullClick` (16089-16102; 16430-16435 then stages a **second** Cash row for `_GetBalance(Cash)`, normally 0 — open question), the denomination buttons `btnCash1..6` (row staged at 16437-16443), the Tab Auth / Tab Complete buttons (4255/4266), full Discount (15983), EBT (16035/16086), Reward (16235). `sender==btnClear` is not a click: `btnPayClick` substitutes it when a dialog answered "Yes" (15677/15780) and its only effect is `mCanPrint=false` (16687).

Guards (no DB change unless stated): staff prompt (16290); the ScanToPay "already paid online" check is dead (boxed Int16 vs Int32, 16305-16327); customer required for Delivery/DeliveryService without 3PDS or `ReqCustomer` (16332-16352; `btnFindCustomerClick` can `UPDATE [Customers] SET [ClubNumber]`, 7784-7788); a tab created on another terminal → `SaveOrder`, `Clear()` (`InUseID=NULL`), exit (16367-16390, 16528-16547); `mRows` = non-deleted rows with `Status=TabAuth=11`, or `Amount<>0` and `Status` ∈ {Open=0, RemoteError=5} (16445-16451); a complementary order (`_Total==0 && _BaseAmount>0`) auto-adds a `Discount=5` row and sets every line `PriceSell=Price` (16460-16476); coupons above `_BaseAmount` abort (16486-16497); with a floor-plan table Credit rows run last (16503). Then **`if (Modified) SaveOrder(sender)`** (16635-16637): Orders/OrdersDetails/OrdersTax and one `Payments` row per staged tender with `Status=0, RefundAmt=0` are on disk **before** any processor call; stock is already decremented. On Account links its CRM transaction to `MainID` or deletes it and jumps to `WithError` (16644-16665).

### 5.2 Tab Complete pre-calculation (16549-16627, only when `!Modified`)

With tender discount/fee enabled for Credit: `_TFUsed==0` → `_UpdatePay(Credit, tab row, _OriginalBal)` and, under dual price, `UPDATE [Payments] SET [Amount]=DualPriceOrderModel.Total WHERE [PayID]=_TabAuthID` (16572); then, when `!IsDualPrice`, `UPDATE [Orders] SET [Tax],[TDUsed],[TFUsed],[Total]; UPDATE [Payments] SET [TDUsed],[TFUsed] WHERE [PayID]=_TabAuthID` (16586-16589, batch). Without TD/TF: `UPDATE [Payments] SET [Amount]=_Balance WHERE [PayID]=_TabAuthID` (16619). When the order **is** modified none of this runs — `SaveOrder` rewrites the tab row instead (1927-1954, 2613).

### 5.3 CFD tip and signature, before the card is read (16741-16813)

On multitouch registers (`Global.TouchScreen` and order type Regular/PickUp/TakeOut, 16691) with tips or CFD signature enabled and a card-like tender, `Global.CFD.TipAndSignature` runs first. A non-zero tip executes one batch: `UPDATE [Orders] SET [Total]=[Total]+@Amount; INSERT INTO [OrdersTips] (OrderID, PayID=row PayID, StaffID, Amount, Type=CFD=7, Value) OUTPUT INSERTED.[TipID]` (16794-16798, wrapper; TipID kept in `mValues.Object4`). The DB `Orders.Total` always gains the tip; in memory only when `!_HasSplitPayments` (16768-16770). Cancel → `WithError`.

### 5.4 Per tender type

| Tender | What happens before the commit | DB writes specific to the type |
|---|---|---|
| **Cash** (0) / DeliveryService (16) (16820-16879) | `frmCash` prompt when `Value` is NULL (cancel → `WithError`, the Cash row stays `Status=0` because WithError deletes card-like types only); drawer opens once per click for the first Cash row (16863-16867, no-op in DEBUG) | dual price after a card tender: `Payments.TFUsed` (csDualPriceManager.cs:278 via 16858) |
| **Credit/Debit/EBTCash/EBTFood/CardOnFile — new payment** (17108-17336) | `CardData{OrderID=MainID, PayID=row PayID, AccountType via Lib.GetAccountTypeFromPayType (Credit=2, Debit=0, EBTCash=3, EBTFood=5, CardOnFile=2, Offline=6), Amount, TxType=Return=5 (exchange) / Auth=0 (restaurant Credit, mIsAuth 16894) / Sale=2}` (17120-17131); `Global.Processors.Process` (17330) drives the device (fake in this build) | on approval `SaveTrx`: `INSERT INTO [CCTxs] (…) OUTPUT INSERTED.[CCTxID]` with `Status = Auth ? Authorized=0 : Completed=1`, `TxType`, `AType`, `AuthCode`, `PNRef`, `TransmitID`, `CCNumber='****'+last4`, `Partial=Amount>AuthAmount`, `Source` (MANUAL=0 when manual), `Tip=IIF(0,NULL)`, `Surcharge` NULL when 0 (csPayment.cs:93-104), then `UPDATE [Payments] SET [CCTxID],[Reference]=@AuthCode WHERE [PayID]` (112-118) — batch A later overwrites `Reference` with the staged row value (NULL for cards), so only `CCTxs.AuthCode` keeps the code; decline → `INSERT [CCTxFailures]` (132-140, 676-697); processor surcharge added to the row Amount (17346-17351); dual price `Payments.TFUsed` + `Orders.Total` (csDualPriceManager.cs:278/301 via 17379-17380); first online Credit approval stamps `UPDATE [Terminals] SET [AuthDate]=GETDATE(), [Sent]=NULL WHERE [AuthDate] IS NULL` (17454) |
| — manual card (17264) / Card on File (17283) | `frmReadCardManual` (`Source=MANUAL=0`); Card on File binds the CRM token and is rewritten to Credit with `ManualCard=true` (17285-17301) | as above; `Payments.Type` was already stored as 3 by SaveOrder |
| — offline (17145-17259) | CardOnFile/Debit/EBT refused; manual entry; per-order/total/days limits; `Global.MainForm.Offline(true)` persists offline mode to `Exatouch.cfg` (17211); fake `AuthCode` (GUID chars with `O` at index 2 and `L` at 4, 17228), `CardData.Reference='Offline'` (never copied to the row: `Payments.Reference` ends NULL), response `OFFLINE` | `SaveTrx` (17235, result ignored; `CCTxs.Status=0` when restaurant Auth, `TxRespons='OFFLINE'`/NULL) then batch `UPDATE [CCTxs] SET [Number]=RSA(PAN+FS+Address+FS+Zip); UPDATE [Payments] SET [Type]=Offline=11; UPDATE [Terminals] SET [OffDate]=GETDATE(),[OffSent]=NULL WHERE [OffDate] IS NULL` (17237-17255). A restaurant offline credit sale ends as `CCTxs.Status=0` / `Payments.Status=6` |
| — partial approval (17354-17443) | live on PAX/Dejavoo (`Default.SemiIntegrated` skips csPayment's auto-void, csPayment.cs:832-867): `frmPartialApproval` | Cancel: `UPDATE [Payments] SET [Amount]=AuthAmount` (17395), `VoidAuth(PayID,true,true)` (17403: CCTxs `Status=3, Tip=0, TxType=6`; Payments `Status=9, CloseDate`; Orders/OrdersTips untouched because `pVoidOrder=true`; `Terminals.OffDate/OffSent=NULL`), optional TD/TF recompute + `SaveOrder` (17416-17423), then `WithError` (row not soft-deleted because `mPartial`). Accept: `UPDATE [CCTxs] SET [CloseDate]=GETDATE(),[Status]=1,[Tip]=0,[TxType]=Capture=1` (17432), row `Amount=AuthAmount, Value=Amount`, `mIsAuth=false` |
| — **Tab Auth initiate** (17464-17540, restaurant) | label from cardholder or `frmLabel`; `Value=_TabMin` authorised | batch `UPDATE [CCTxs] SET [Status]=TabAuth=2; UPDATE [Orders] SET [TabAuthID]=@PayID,[TerminalID]=Global.TerminalID,[Label]=ISNULL([Label],@Label),[TabLimit]=(partial ? AuthAmount : NULL),[TabMin]=_TabMin; UPDATE [Payments] SET [Status]=TabAuth=11,[Value]=AuthAmount` (17486-17514); FloorPlan `Status=IIF(Status=10,10,7), LastAct=GETDATE()` (17521) or `DBUpdate` (17532); receipt; `_DefaultScreen()` → `InUseID=NULL` (17537); **return** — `Orders.Status` stays 0, `Paid` unchanged, `Payments.CloseDate` NULL |
| — **Tab Complete** (16904-17107) | `Global.Processors.Complete(_TabAuthID, tip)` (16915; `tip=null` non-multitouch, `_TipAmt` multitouch) captures: returns -1 when already captured (csPayment.cs:330-337), tip cancelled/over cap (353-374), disconnected (396-401) or device failure (458-464, `CcTxFailures` row) | success: `CCTxs SET CloseDate=GETDATE(), PNRef, Status=1, TransmitID, Tip, TxType=Capture=1` (csPayment.cs:483-491); with `tip==null` also `Payments SET CloseUserID=Orders.StaffID, Amount+=Tip, Status=1; Orders SET Paid+=Tip, Tip+=Tip, Total+=Tip` (495-509); surcharge rows (517-532); `OrdersTips` INSERT/UPDATE `Type=CC=1` (or CFD=7 when TouchScreen) (547-561); TouchScreen `Payments.Amount+=Tip` **again** (565-569); `FeeFromTips` (575-579). Then tender fee on → `Payments.TFUsed` (17014) + `Orders.TFUsed` (17024); else surcharge on → batch `Payments.Amount=row Amount+Surcharge; CCTxs.Surcharge; Orders.Surcharge=SUM` (17061); always `UPDATE [Orders] SET [TabAuthID]=NULL` (17085); offline tab → `Payments.Type=Credit=3` (17097); `mIsAuth` cleared (16995). Failure (-1): TD/TF zeroed in memory, `UPDATE [Orders] SET [Tax],[TDUsed],[TFUsed],[Total]; UPDATE [Payments] SET [TDUsed]=0,[TFUsed]=0` (16956) when TD/TF applies, and **return** — the tab row stays 11, `TabAuthID` intact |
| — terminal tip (17546-17594) | non-multitouch, `Account.TipReq`, PIN-pad tip | batch `UPDATE [Orders] SET [Total]=[Total]+@Tip; UPDATE [CCTxs] SET [Tip]=NULL; INSERT [OrdersTips] (…, Type=Terminal=5) OUTPUT TipID` (17554-17562); row `Amount+=tip`, `mIsAuth=false` |
| **On Account** (19) (17605-17620) | CRM INVOICE already created at staging | dual price only: `Payments.TFUsed` (17615) |
| **eGift** (7) (17622-17748) | negative amount → queued exchange; `frmGiftCard` (cancel → WithError), optional `GetBalance` (17664), `GiftCardManager.Redeem` (17678, HTTP; Training mock approves); host "101" = partial (`Amount=mGift.Redeem`, `mPartial=true`); `mIsAuth = IsRestaurant && balance<>0`, `Value`=remaining balance (17693-17694) | `SaveTrx` (17726: CCTxs `CardHolder='eGift'`, `AType=GiftCard=4`, `AuthCode`=host reference, `TxType` Auth/Sale; Payments `CCTxID, Reference`); local `GiftCards` row decremented if the number matches a local `GiftCode` (`Balance-=Redeem, Used=(Balance<=0), UsedDate`, GiftCardManager.cs:105-118 via 17732); `CCTxs.Number`=RSA(card) via Dapper full-row UPDATE (CardTransactionManager.cs:28 via 17735); dual price `TFUsed` (17744). Batch A then overwrites `Payments.Reference` with the row value (NULL) — the host reference survives only in `CCTxs.AuthCode` |
| **Certificate** (12) (17753-17840) | code from `Reference` or `frmLabel` "Gift Cert #:"; not found / zero balance → `_CancelGiftCertificatePayment` → `_RemovePayment` → `RowNotInTableException` at 12598, **aborting the handler before WithError** (certificate row stays `Status=0`, global error popup); balance below amount → `Amount=balance, Value=0, mPartial=true` (17813-17818) else `Value=balance−amount` (17826) | `GiftCardManager.ApplyPayment`: `GiftCards SET Balance-=amount, Used=(Balance<=0), UsedDate=now` (GiftCardManager.cs:105-118 via 17829, Dapper); dual price `TFUsed` (17836) |
| **Reward** (10) / Factor4Loyalty (17) (17845-17924) | only inside `if (_RewardEnabled)` — otherwise committed as an ordinary Completed payment with no deduction; Factor4 → csLoyaltyCard API (17857/17869); EPI: `mUsed=ceil(abs(amount))×DollarPerPoint` (17878-17880), refused when > `Lib.GetPoint` | EPI: per candidate row (no ORDER BY, 17891) `UPDATE [Loyalty] SET [Used]=IIF([Used]+@Used<[Points],[Used]+@Used,[Points]) OUTPUT INSERTED.[Used]-DELETED.[Used] WHERE [LoyaltyID]` until consumed (17907-17919); `Value=mUsed` |
| **Check** (1) / **PayOutCust** (13) / **CheckCashBack** (14) | Check rows are skipped in the loop when a cash-back amount exists (16717); PayOutCust rows only get `PaymentManager.UpdateTip` (`UPDATE [Payments] SET [Tip]=@Tip`, PaymentManager.cs:25-31 via 16730) | finished in the cash-back section (5.6) |
| **Coupon** (2) / **Discount** (5) / **ClubDiscount** (15) | rows exist in `Payments` with `Status=0` from SaveOrder | batch A updates the row; the `Orders` half of the batch is **omitted** (17955) — they never add to `Orders.Paid` |

### 5.5 The per-tender commit (batch A, 17928-18036)

After the `switch`, for every tender reaching the end of the loop body, one CommandText via `Lib.ExecNonQuery` (18036, **swallows** SqlException):

```sql
UPDATE [Payments] SET [BankID]=@BankID, [TerminalID]=@TerminalID, [UserID]=@UserID
    /* , [Amount]=@Amount, [Tip]=@Tip  -- omitted when the row's in-memory Status is TabAuth=11 (17942) */,
    [CloseDate]=GETDATE(), [DeliveryFee]=@DeliveryFee, [Reference]=@Reference, [Status]=@Status, [Value]=@Value, [Surcharge]=@Surcharge
WHERE [PayID]=@PayID;
UPDATE [Orders] SET [Paid]=ISNULL([Paid],0)+@OrderPaidAmount /* omitted for exchanges */,
    /* [Total]=ISNULL([Total],0)+@Surcharge, [Surcharge]=(SELECT SUM([Surcharge]) FROM [Payments] WHERE [OrderID]=@OrderID), only when this payment's Surcharge<>0 */
    [Status]=@OrderStatus  /* OrderStatus.Pending=6 */
WHERE [OrderID]=@OrderID;   -- whole second statement omitted for ClubDiscount/Coupon/Discount (17955)
UPDATE [Payments] SET [FeeFromTips]=… WHERE [PayID]=@PayID;   -- Credit/Debit with DeductFeeFromTips and a tip share (18008)
```

`@Status` = `Refunded=8` for exchanges (`Amount<0`), `Auth=6` when `mIsAuth` (restaurant Credit not yet captured; restaurant eGift with balance left), else `Completed=1` (17967). `@BankID=Global.LocalSetting.BankID ?? NULL`, `@TerminalID=Global.TerminalID`, `@UserID=Global.UserID`, `@Value=row Value`, `@Surcharge` NULL when 0 (17971-17982). **`@Tip`** = this tender's share of `(_TipAmt−_TipPaid)` pro-rated by `mAmount/(_TotalPay−mPay)` (or the remainder when it covers the rest), written only when `mAmount >= share`, else NULL; on success `mAmount -= share` (17994-18017). `@DeliveryFee` = remaining driver share when `>0` and `<= mAmount`, else 0 (18024-18033). Then CFD/receipt branching (18040-18272): touch orders call `Global.CFD.Complete` (rating may write `Orders.Rate`, frmCFD.cs:632); non-touch dual-price Cash/Check/DeliveryService writes `TFUsed` and `Orders.Total` (18124-18125). `mPartial` breaks the loop (18274): later rows are not processed.

Order of `Orders.Status` inside one Pay click: `0` (INSERT) → `6` (per tender) → `1` (close batch, background thread). A partially paid order stays at `6` and is listed by frmGetOrders (`Status IN (0,6)`).

### 5.6 Check cash-back / pay-out to customer (18283-18445)

`mValue = _Pay[CheckCashBack] + _Pay[PayOutCust]` net of completed ones. If non-zero, `mDue=_Total−_Paid`; when `mDue<0` a synthetic row `Type=-1, Amount=mDue, Status=Open` is appended (18309-18316). Rows `[Status]=0 AND [Deleted]=0 AND [Type] IN (1,13,-1)` are processed in insertion order: `Amount>=mDue` → `@Amount=mDue`, Check→CheckCashBack=14 when `Amount>mDue`; else `@Amount=row.Amount`. Rows without PayID: `INSERT INTO [Payments] (BankID, OrderID, TerminalID, UserID, Amount, CloseDate=GETDATE(), RefundAmt=0, Status=1, TDUsed=0, TFUsed=0, Type=Cash=0, Value=0) OUTPUT PayID` (18352, `Orders.Paid` not adjusted); others: batch `UPDATE [Orders] SET [Paid]+=@Amount; UPDATE [Payments] SET BankID, TerminalID, UserID, Amount=@Amount (applied), Value=row.Amount (tendered), CloseDate=GETDATE(), Reference, Status=1, Type WHERE PayID` (18361-18373, raw). When `mValue >= mTotal−mPaid`: `INSERT INTO [BankMod] (BankID, UserID, MngrID=Global.MgrID, Cash=-mChangeDue, Date=GETDATE(), Notes='Check '/'Pay Out customer '+' from Order: '+MainID)` when a bank is swapped in (18382), drawer, change-due dialogs.

### 5.7 Dual-price reconciliation (18448-18535)

When a DP tender was used and the model balance is 0 (±0.02): form totals are replaced from `DualPriceOrderModel` and rewritten — `Orders` (`BaseAmount, ItemTotal, Tax, [DeliveryFee], Total`, csDualPriceManager.cs:690), `OrdersDetails` price/promo columns per ODID (721), `OrdersTax` (773), `OpenPromoDetails.Value` (823), `OrdersDelivery.DeliveryFee` (878), coupon `Payments.Amount` (923), club-discount `Payments.Amount/TFUsed` (995; when fee<=0 `@TFUsed` is never bound → SQL error, logged). Rows 721-995 log and continue, so they can lag the `Orders` row.

### 5.8 Completion (18562-18972)

`_Balance` is zeroed within 0.02 when fees exist (18562). If `!mPartial && Round(_Balance,2)==0`:

1. If `_HasTabAuth` and `_Payments.Rows[0]` is still TabAuth (the tab was closed with another tender): `Global.Processors.VoidAuth(_TabAuthID,false,true)` (18572): device void (Dejavoo captures first), then batch `CCTxs SET Status=3, Tip=0, TxType=6; Payments SET Amount=IIF(Status=11,Value,Amount), CloseDate=GETDATE(), Status=9; Orders SET TabAuthID=NULL` (csPayment.cs:1105-1148) and `Terminals SET OffDate=NULL, OffSent=NULL` (1173-1181, always for online voids); failure only warns.
2. Locals captured (18581-18617): `mIsAuth`/`mPayType` hold the **last processed tender's** values; `mUserID=Global.UserID`; `mGifts` = detail rows with ItemID 5/6 plus exchange rows.
3. **Background thread** (18620) runs batch C via `Lib.ExecScalar` (18671, swallows): `DELETE FROM [OrdersTimeCharge] WHERE [OrderID]; INSERT INTO [OrdersLog] (OrderID, ID=@StaffID=Global.UserID, Type=Close=1); INSERT INTO [OrdersPromoDetails] (ODID,Type,Value) SELECT … FROM [OpenPromoDetails] WHERE [OrderID]; DELETE FROM [OpenPromoDetails] WHERE [OrderID]; UPDATE [Orders] SET [BatchID]=@BatchID (literal 0, NULL when a tax-code item exists — not the bank), [CloseDate]=GETDATE(), [Status]=Completed=1, [Tip]=@Tip (=_TipAmt, the whole order tip), [Surcharge]=@Surcharge (SUM(Payments.Surcharge) via Dapper when TenderFee.Surcharge, NULL when 0) OUTPUT INSERTED.[FPID]` (18622-18671). This runs **also when payments are still Auth=6** (restaurant): the order is Completed while its card payment awaits capture.
4. Customer (same thread): EPI `INSERT INTO [Loyalty] (CustomerID, OrderID, Points=int(mLoyalty)×PointPerDollar, Used=0)` (18713, only with `_RewardEnabled`, `_Loyalty<>0`, `Customers.Loyalty=1`, `Loyalty.Selected`, not Factor4) or Factor4 API (18700); club sale `UPDATE [Customers] SET [Club]=_ClubNew, [ClubNew]=NULL, [ClubEnd]=…, [ClubStart]=ISNULL([ClubStart],today)` (18729); visit stats `UPDATE [Customers] SET [FirstVisit]=ISNULL(...), [HowManyVisits]+=1, [PrevOrder]=[LastOrder], [LastOrder]=@OrderID, [RecentVisit]=GETDATE()` (18741).
5. Gift items: each `ItemID=GiftCard=6` line → `GiftCardManager.Activate` (18777, HTTP); `IssueCertificate` converts it to `Certificate=5`; `CanceledByUser` fires `OrderManager.VoidOrder` via `BeginInvoke` (18793 — the **only** caller of the transactional void, section 6.1). Certificate rows: `INSERT INTO [GiftCards] (CustomerID=Lib.ToInt(_CustomerID) (0 for walk-in), OrderID, UserID, Amt=Balance=Price, DateSold=now, FromRefund=0, Used=0, GiftCode) ; SELECT SCOPE_IDENTITY()` (GiftCardManager.cs:370-402 via 18816; amount<=0 throws `ArgumentOutOfRangeException` on the thread) then `UPDATE [OrdersDetails] SET [ItemID]=5, [Description], [SKU]=GiftCode WHERE [ODID]` (OrderManager.cs:232 via 18824).
6. Floor plan via `BeginInvoke` (18839-18925): Split table + `mIsAuth` → `UPDATE [OrdersFPSplit] SET [Status]=Authorized=5 WHERE [OrderID]` (18868); Split table otherwise → `DELETE FROM [OrdersFPSplit] WHERE [OrderID] AND [FPID]; SELECT TOP 2 remaining` (18877) and, when exactly one remains, `DELETE … WHERE [FPSID]` (18895); then `csObject.DBUpdate` → `FloorPlan SET OrderID, InUse=0, Status, LastAct=GETDATE()` — 0 split rows left → `OrderID=NULL, Status=CleanUp=6`; 1 left → that order and its status; non-split `mIsAuth` → `Status=Authorized=5`; else `OrderID=NULL, Status=6` (csObject.cs:472 via 18890/18902/18923). **If no csObject control for the table is on `pnlObjects`, the fallback `new csObject()` has `ID=DBNull` and `DBUpdate` touches 0 rows** (18849).
7. Prints (remote ticket when `GoPrintOrder`, gift certificate, receipt — `csPrint.cs:500` may `UPDATE [Payments] SET [TerminalID]` when NULL), Change Due (18930-18941).
8. Meanwhile the **UI thread** sets `Modified=false` and runs `Clear()` (directly for a Split table then `_CurrentObj.DoClick()`, else via `_DefaultScreen(1)`) → `UPDATE [Orders] SET [InUseID]=NULL` (739), optional `_Lock()` (18950-18971). Not complete → `_TotalUpdate/_PayUpdate` only; `Orders.Status` stays 6.

### 5.9 `WithError` (18982-19113)

Non-partial, non-TabAuth tenders of Type Credit=3/Debit=4/EBTCash=18/EBTFood=6/eGift=7/Offline=11/OnAccount=19/Certificate=12 are soft-deleted: `UPDATE [Payments] SET [OrderID]=-[OrderID], [Status]=Deleted=14 WHERE [PayID]` (18996; dead for Certificate, 5.4). A CFD tip (`mValues.Object4`) is reverted from `Orders.Total` **for eGift only** (`Orders.Total=@Total-@Tip`, 19028) and its `OrdersTips` row negated (`OrderID=-OrderID`, 19040); `Orders.Status=Pending=6` when something was paid and a row is Completed in memory (19058); tender-discount/fee tenders get `Orders.TDUsed/TFUsed/Tax/Total` rewritten (19093). Cash rows are **not** deleted (they stay `Status=0` with a PayID); loyalty points consumed at 17907 are never restored.

### 5.10 Completing an authorised payment later (`Auth=6`)

Restaurant card sales leave `Payments.Status=6`, `CCTxs.Status=0/TxType=0` while `Orders.Status=1`. They are captured by: **Complete window** — `Complete(object)` 1061 → `btnCompleteClick` 13429 → `frmComplete` → `_OnComplete(pOrderID,pPayID,pTip)` (9230-9435; candidates = Payments JOIN CCTxs with `CCTxs.Status=0`, semi-integrated processors need `TID` NULL or this terminal's, Delivery orders need `OrdersDelivery.Status` Out/Delivered) → `Global.Processors.Complete(PayID, null)` (9324; csPayment rows: CCTxs capture 483-491; Payments `Status=1, Amount+=tip, CloseUserID=Orders.StaffID`; Orders `Paid/Tip/Total += tip` 495-509; surcharge 517-532; OrdersTips 547-561; TouchScreen second add 565-569; FeeFromTips 575-579), then for Delivery orders `UPDATE [OrdersDelivery] SET [Delivered]=GETDATE(), [Status]=3 WHERE [ODELID]` (9338), and floor-plan cleanup (9348-9432: `DELETE [OrdersFPSplit]` + `FloorPlan SET OrderID=NULL, InUse=0, LastAct=NULL, Status=6` when no control is on screen, 9360-9424). **Delivery manager** credit branch (frmDelManager.cs:659 → `Complete(PayID, tip)` with a decimal tip, so the Payments/Orders amount rows 495-509 are **skipped**; frmDelManager then runs batch H `UPDATE [Orders] SET [Tip]+=@Tip,[Paid]+=@Tip,[Total]+=@Tip; UPDATE [Payments] SET [Amount]+=@Tip,[CloseUserID]=Global.UserID,[Status]=1`, 663-681). **Batch Close** (this build, csPayment.cs:165-193, no terminal or status filter): `UPDATE [Payments] SET [CloseUserID]=Global.UserID or [UserID], [CloseDate]=ISNULL([CloseDate],@Now), [Status]=IIF([Status]=6,1,[Status]) FROM Payments JOIN CCTxs … WHERE BatchClose IS NULL AND Number IS NULL; UPDATE [CCTxs] SET [BatchClose]=@Now, [CloseDate]=IIF(Status=0,@Now,CloseDate), [Status]=IIF(Status=0,1,Status), [TxType]=IIF(TxType=0,1,TxType) WHERE BatchClose IS NULL AND Number IS NULL` — TabAuth rows are stamped too and their TxType flips to Capture, after which `Complete` refuses them (330) while `VoidAuth` still works. **Pay-at-table** (`spApplyPayment`) is a separate device path (section 8).

### 5.11 Write list of `btnProcessClick`, in execution order

| # | file:line | table | write | condition / exec |
|---|---|---|---|---|
| A1 | 739 via 16385/16543/16675 | Orders | `InUseID=NULL` | exits: tab owned elsewhere; locked register with no items (raw) |
| A2 | 7784 via 16345 | Customers | `ClubNumber` | ReqCustomer path (wrapper) |
| A3 | SaveOrder via 16371/16530/16637 | all | section 3.12 | `Modified` / tab-elsewhere exits |
| A4-A6 | 16572 / 16589 / 16619 | Payments; Orders | tab pre-calc (5.2) | Tab Complete, `!Modified` (raw) |
| A7 | PaymentManager.cs:25 via 16730 | Payments | `Tip` | PayOutCust row (Dapper) |
| A8 | 16794 | Orders; OrdersTips | `Total+=tip`; INSERT Type=7 | CFD tip (wrapper batch) |
| A9 | csDualPriceManager.cs:278 via 16858 | Payments | `TFUsed` | Cash, dual price after card (raw) |
| A10 | csPayment.cs:483-579 via 16915 | CCTxs; Payments; Orders; OrdersTips | capture (5.4 Tab Complete) | wrapper per statement |
| A11 | 16956 | Orders; Payments | TD/TF zeroed | Tab Complete failed (raw batch) |
| A12-A13 | 17014 / 17024 | Payments; Orders | `TFUsed` | tab complete OK, tender fee (raw) |
| A14 | 17061 | Payments; CCTxs; Orders | surcharge | tab complete OK, surcharge (raw batch) |
| A15 | 17085 | Orders | `TabAuthID=NULL` | after successful capture (raw) |
| A16 | 17097 | Payments | `Type=3` | tab opened offline (raw) |
| A17 | csPayment.cs:94,113 via 17235 | CCTxs; Payments | offline SaveTrx | wrapper |
| A18 | 17237 | CCTxs; Payments; Terminals | `Number`; `Type=11`; `OffDate` | offline (raw batch; throws if A17 failed) |
| A19 | csPayment.cs:94,113 via 17330 | CCTxs; Payments (+CCTxFailures) | online SaveTrx | device approved |
| A20 | csDualPriceManager.cs:278/301 via 17379-17380 | Payments; Orders | `TFUsed`; `Total` | dual price (raw) |
| A21 | 17395 | Payments | `Amount=AuthAmount` | partial cancel (raw) |
| A22 | csPayment.cs:1105-1176 via 17403 | CCTxs; Payments; OrdersFPSplit; Terminals | VoidAuth (`pVoidOrder=true`) | partial cancel |
| A23 | SaveOrder via 17423 | all | recomputed TD/TF | partial cancel with fees |
| A24 | 17432 | CCTxs | capture | partial accepted (raw) |
| A25 | 17454 | Terminals | `AuthDate=GETDATE(), Sent=NULL` | online Credit, first time (raw) |
| A26 | 17486 | CCTxs; Orders; Payments | tab auth statuses | Tab Auth (raw batch) |
| A27 | 17521 / csObject.cs:472 via 17532 | FloorPlan | `Status=7` | Tab Auth, floor plan |
| A28 | 739 via 17537 | Orders | `InUseID=NULL` | Tab Auth created (raw) |
| A29 | 17554 | Orders; CCTxs; OrdersTips | terminal tip Type=5 | TipReq (wrapper batch) |
| B1 | csDualPriceManager.cs:278 via 17615 | Payments | `TFUsed` | OnAccount, dual price |
| B2 | csPayment.cs:94-118 via 17726 | CCTxs; Payments | eGift SaveTrx | eGift redeemed |
| B3 | GiftCardManager.cs:116 via 17732 | GiftCards | `Balance-=`, `Used`, `UsedDate` | eGift with local row (Dapper) |
| B4 | CardTransactionManager.cs:28 via 17735 | CCTxs | `Number` | eGift (Dapper full-row) |
| B5 | csDualPriceManager.cs:278 via 17744/17836 | Payments | `TFUsed` | eGift/Certificate dual price |
| B6 | GiftCardManager.cs:116 via 17829 | GiftCards | `Balance-=amount` | Certificate (Dapper) |
| B7 | 17908 | Loyalty | `Used` | Reward EPI (wrapper per row) |
| B8-B10 | 17938 / 17959 / 18008 | Payments; Orders; Payments | batch A (5.5) | every tender (wrapper, swallows) |
| B11 | csDualPriceManager.cs:278/301 via 18124-18125 | Payments; Orders | `TFUsed`; `Total` | non-touch DP cash/check |
| B12-B14 | 18352 / 18361 / 18382 | Payments; Orders+Payments; BankMod | cash-back (5.6) | wrapper / raw / raw |
| B15-B19 | csDualPriceManager.cs:690/721/773/823/878/923/995 via 18485-18531 | Orders; OrdersDetails; OrdersTax; OpenPromoDetails; OrdersDelivery; Payments | DP rewrite (5.7) | raw / logged |
| B20-B21 | csPayment.cs:1105, 1173 via 18572 | CCTxs; Payments; Orders; Terminals | void leftover tab pre-auth | wrapper |
| B22-B23 | 18623-18644 | OrdersTimeCharge; OrdersLog; OrdersPromoDetails; OpenPromoDetails; Orders | batch C (5.8) | background thread, wrapper |
| B24-B26 | 18713 / 18729 / 18741 | Loyalty; Customers; Customers | accrual; club; visits | customer set (raw/raw/wrapper) |
| B27-B28 | GiftCardManager.cs:393 via 18816; OrderManager.cs:232 via 18824 | GiftCards; OrdersDetails | certificate sold | Dapper |
| B29-B31 | 18868 / 18877+18895 / csObject.cs:472 via 18890-18923 | OrdersFPSplit; FloorPlan | floor plan release | raw / wrapper |
| B32 | 739 | Orders | `InUseID=NULL` | UI thread, concurrent with B22 (raw) |
| B33-B36 | 18996 / 19028+19040 / 19058 / 19093 | Payments; Orders+OrdersTips; Orders; Orders | WithError (5.9) | raw |
| B37-B41 | csPayment.cs:502/527/548 via 9324; 9338; 9360; 9377; 9401/9414 | Orders; OrdersTips; OrdersDelivery; OrdersFPSplit; FloorPlan | `_OnComplete` (5.10) | raw |

## 6. Voiding

Four different mechanisms void things; they touch different rows and only one is transactional.

| Mechanism | Reached from | What it voids | Transaction |
|---|---|---|---|
| `OrderManager.VoidOrder` (OrderManager.cs:56-221) | **only** frmRegister.cs:18791-18797, via `BeginInvoke`, when a just-completed order's eGift line failed `GiftCardManager.Activate` (`CanceledByUser`) | the whole completed order: payments, card transactions, stock, order, floor plan, certificate re-issue | `XTUnitOfWork(true)` — yes |
| `csPayment.VoidAuth(pPayID, pCheckGift=true, pVoidOrder=false)` (csPayment.cs:961-1186) | frmComplete Void (frmComplete.cs:257) and frmReView Void (frmReView.cs:213) with `(true,false)`; floor-plan Join of a TabAuth table (14918, `(true,false)`); partial-approval cancel (17403, `(true,true)`); order closed with another tender while a tab pre-auth is open (18572, `(false,true)`); frmRefund card rows in Void mode or Auth payments (frmRefund.cs:2250, `(true,true)`) | one card/gift payment (and, with `pVoidOrder=false`, the order's `Paid/Status/Tip/Total` and that payment's tips) | no — one batch, wrapper |
| frmGetOrders **Void** (frmGetOrders.cs:384-484) | Register → Get Order → select an **Open** (`Status=0 AND TabAuthID IS NULL`) order → Void (`Register.VoidOrder` security; reason prompt when `RefReqReason`, else `ReasonID=40`) | an open, unpaid-or-partly-paid order: no processor call, Open payments are marked Deleted | no — one batch, wrapper ExecReader |
| frmRefund **Void** (frmRefund.cs:1875-3034, `mReversalType=Void`) | Register → Refund → load a completed order → Void (`_Ref==_Total`, no Debit/EBT payment, no batch-closed card, `RefundAmt==0`, `Register.VoidOrder` security) | a completed paid order: processor void per card payment, `Orders.Status=5`, optional Re-create | no — raw statements |

Plus the reversal paths that are not called "void": `frmOfflineProcess` declined settlement (section 9.9) and `Lib.JoinOrder`'s `Payments.Status=Voided=9` for ClubDiscount rows of a joined source (Lib.cs:4944).

### 6.1 `OrderManager.VoidOrder` (transactional)

Step 1: `new XTUnitOfWork(true)` (64). Step 2: `SELECT * FROM Orders WHERE OrderID=@Id`; `SELECT * FROM Payments WHERE [OrderID]=@OrderId AND [Amount]>0 AND ([Status]=1 OR [Status]=6)` (PaymentManager.cs:41-48; TabAuth=11 rows are never selected, so `Orders.TabAuthID` is never cleared here — dead branch 171-172; no ORDER BY). Step 3, per payment (70-157): Credit=3/Debit=4/EBTCash=18/EBTFood=6 → `PaymentManager.VoidCardPayment` (PaymentManager.cs:54-141): throws if `Payments.CCTxID` is NULL, NREs on a missing CCTxs row, returns **false with no processor call when `!Global.IsConnected`** (65-68); `AType=GiftCard=4` → `GiftCardManager.AddValue` HTTP; else `Process(CardData{TxType=Void, Amount=Payment.Amount (Value if 0), Tip=CCTxs.Tip, AuthCode, RefCode=PNRef, TransactionID=TransmitID})`. The abort test (82-85) throws `VoidPaymentException` only when the type is not Offline AND the void returned false AND `Processors.Default.ResponseMessage` is non-blank AND not "already voided" — so an offline terminal, an uninitialised processor or a failed gift AddValue yields a **committed local-only void**. Offline=11 skips the processor. eGift=7 payments throw (`AllowGiftCardPayments=false`, 136-137); Factor4Loyalty=17 needs `Orders.CustomerID` and calls the Factor4 SOAP refund; Check/Coupon/Discount/PayIn/OnAccount/CardOnFile are just marked Voided (no CRM reversal for OnAccount). Step 4 (159-165): every `OrdersDetails` row with `ItemID` and `Qty` not NULL (Deleted rows included, **no StockType filter**, pseudo-items 1-12 and modifiers included) → `ItemRepository.UpdateQtyOnHand`: `QtyOnHand += Qty` then a 141-column full-row `Items` UPDATE (ItemRepository.cs:34-40; NULL stays NULL; missing row → NRE → rollback). Step 5: `Orders` full-row UPDATE with `Status=Voided=5, RefundAmt=Paid (as read in step 2), VoidDate=DateTime.Now` (167-174; all other columns rewritten from the step-2 snapshot). Step 6: with `License.FloorPlanEnabled` and `Orders.FPID`, if `FloorPlan.OrderID` matches and `Status<>Split=10`: full-row UPDATE `Status=CleanUp=6` (`OrderID` **not** cleared, 176-183). Step 7: `mCertVoided>0` (Certificate=12 / Reward=10 amounts) → `GiftCardManager.CreateGiftCertificate` on the same UoW: `INSERT INTO [GiftCards] (CustomerID=Orders.CustomerID, OrderID, UserID, Amt=Balance=Σ, DateSold=now, FromRefund=1, Used=0, GiftCode='3'+8 digits, …); SELECT SCOPE_IDENTITY()` (187-196). Step 8: `Commit` (198). After commit: drawer when `OpenAlwaysCD` or cash was voided, `Lib.Success("Transaction voided" …, pAutomaticClose=false)` — **must be dismissed by automation** (210), CFD message, `Print.GiftCert`, `Print.Refund(OrderId)`.

| # | file:line | table | write | condition |
|---|---|---|---|---|
| V1 | OrderManager.cs:108 | CCTxs | full-row: `RAmt=Payments.Amount, Status=3, Tip=0` (+`Number=NULL, OffLineStatus=4` for Offline) | per card/EBT/Offline payment after the processor step |
| V2 | OrderManager.cs:120 | Terminals | full-row: `OffDate=NULL, OffSent=NULL`; `NoOffline(false)` | after V1 when no `CCTxs` of this terminal has `Number IS NOT NULL` (any historical eGift row blocks it) |
| V3 | OrderManager.cs:156 | Payments | full-row: `Status=9, RefundAmt=Amount, CloseDate=now` (`CloseUserID, BankID, Tip` unchanged) | every selected payment |
| V4 | ItemRepository.cs:39 via OrderManager.cs:164 | Items | full-row: `QtyOnHand += Qty` | per detail row with ItemID and Qty |
| V5 | OrderManager.cs:174 | Orders | full-row: `Status=5, RefundAmt=Paid, VoidDate=now` | always |
| V6 | OrderManager.cs:183 | FloorPlan | full-row: `Status=6` | floor plan, FPID, OrderID matches, not Split |
| V7 | GiftCardManager.cs:393 via OrderManager.cs:189 | GiftCards | INSERT `FromRefund=1` | `mCertVoided>0` |
| V8 | OrderManager.cs:198 | — | COMMIT; any exception before it rolls V1-V7 back (processor voids, gift credits, Factor4 refund and `CcTxFailures` rows are **not** rolled back) | |

**Absent**: no `OrdersLog`, `OrdersTips`, `OrdersFPSplit`, `OrdersTax` or `Customers` change; no socket broadcast; `Orders.ReasonID/CloseDate/Note/InUseID` and `Payments.CloseUserID` untouched. Because of `BeginInvoke` the void runs after `btnProcessClick` returned; its exceptions surface as a `Lib.Error` dialog via `Application.ThreadException` (Start.cs:233-253).

### 6.2 `csPayment.VoidAuth` (one payment)

Reads `Payments`+`CCTxs` (csPayment.cs:964-973): `Amount = Payments.Amount` or `Value` when 0 (979), `Tip = CCTxs.Tip` (981), `IsTabAuth = Payments.Status=11` (980), offline = `Payments.Type=11` (985). Online needs `Global.IsConnected` (991). With `pCheckGift`: no writes if the order has a used gift certificate (1015) or an eGift line with balance below `PriceSell` (1021-1048). Non-gift: processor Void (1066-1082; Dejavoo TabAuth captures first, 1058-1064); gift: `GiftCardManager.AddValue` (1094). Offline goes straight to SQL (986-987). Returns false **without a message** when disconnected, on the gift checks, on `Init` failure or a failed AddValue — callers that test only the bool then persist their own "voided" state without any processor void (frmRefund.cs:2255 hazard). Then one batch via `ExecScalar` (1105-1173):

```sql
DECLARE @TabID INT;
UPDATE [CCTxs] SET [Status]=3, [Tip]=0, [TxType]=6 /* , [Number]=NULL, [OffLineStatus]=4 when offline */ WHERE [CCTxID]=@CCTxID;
UPDATE [Payments] SET @TabID=IIF([Status]=11,[OrderID],NULL), [Amount]=IIF([Status]=11,[Value],[Amount]), [CloseDate]=GETDATE(), [Status]=9 WHERE [PayID]=@PayID;
IF (@TabID IS NULL) BEGIN
    UPDATE [OrdersFPSplit] SET [Status]=3 /* Ordered */ WHERE [OrderID]=@OrderID;
    SELECT @Count=COUNT(1) FROM [Payments] WHERE [OrderID]=@OrderID AND [Status] NOT IN (14, 8, 9);
    /* only when pVoidOrder=false: */
    UPDATE [Orders] SET [Paid]=[Paid]-@Amount, [Status]=IIF([Status] IN (1,6), IIF(@Count=0, 0, 6), [Status]), [Tip]=[Tip]-@Tip, [Total]=[Total]-@Tip WHERE [OrderID]=@OrderID;
    DELETE FROM [OrdersTips] WHERE [PayID]=@PayID;
END ELSE UPDATE [Orders] SET [TabAuthID]=NULL WHERE [OrderID]=@TabID;
```

Then, whenever `ExecScalar` returned null — **always** for online voids (the batch has no SELECT result) and for offline voids only when no `CCTxs` with `Number` remains (1151-1154) — `UPDATE [Terminals] SET [OffDate]=NULL, [OffSent]=NULL WHERE [TerminalID]=Global.TerminalID` and `NoOffline(false)` (1173-1181). Net effect by caller: **frmComplete/frmReView Void** (`pVoidOrder=false`, non-tab): CCTxs 3/6, Payments 9, `Orders.Paid-=Amount`, `Status` 1/6 → 0 when no live payment remains else 6, `Tip/Total -= CCTxs.Tip`, that payment's `OrdersTips` deleted, `OrdersFPSplit.Status=3`; frmComplete then `RevertAnyGiftCardOrCertificate` for restaurant Auth payments (frmComplete.cs:263: `GiftCards SET Balance=0, Used=1, UsedDate` for certificate lines). **Tab rows** (Status 11 → `@TabID` set): only CCTxs 3/6, Payments 9 with `Amount=Value`, `Orders.TabAuthID=NULL`; `Paid/Status/OrdersTips` untouched (the Join at 14918 and 18572 rely on this). **`pVoidOrder=true`** (17403, 18572, frmRefund): the Orders rollback and the OrdersTips delete are not emitted; the caller owns the order row.

### 6.3 frmGetOrders Void of an open order (frmGetOrders.cs:384-484)

Enabled only for `Status=Open=0 AND TabAuthID IS NULL` rows (521), never in Completed-orders mode (546). No processor call. One CommandText via `ExecReader(SingleRow)` (444):

```sql
UPDATE [Items] SET [QtyOnHand]=[QtyOnHand]+[Qty] FROM [Items] JOIN (SELECT [ItemID], SUM([Qty]) AS [Qty] FROM [OrdersDetails] WHERE [OrderID]=@OrderID AND [Deleted]=0 GROUP BY [ItemID]) [TEMP] ON … WHERE [StockType]<>2;   -- QtySold untouched
INSERT INTO [OrdersLog] ([OrderID],[ID],[Type]) VALUES (@OrderID, @UserID /* Global.UserID */, 6 /* Voided */);
INSERT INTO [OrdersLog] ([OrderID],[ID],[Type]) VALUES (@OrderID, @ManagerID /* Global.MgrID: NULL without an override in Release, 1 in this build */, 2 /* Manager */);
UPDATE [Payments] SET [Status]=14 WHERE [OrderID]=@OrderID AND [Status]=0;
UPDATE [Orders] SET [ReasonID]=@ReasonID, [CloseDate]=GETDATE(), [Note]=@Note+CAST(ISNULL([Note],'') AS VARCHAR(MAX)), [Status]=5 OUTPUT INSERTED.[CustomerID], INSERTED.[FPID] WHERE [OrderID]=@OrderID;
```

A NULL `@Note` (no reason prompt) wipes any existing note. `VoidDate`, `RefundAmt`, `BatchID`, `InUseID` are untouched; already-Completed payments (a partly paid Pending order is not voidable here, but a Completed row on an Open order would keep Status 1). With an `FPID`: Split tables lose their `OrdersFPSplit` row (`DELETE … WHERE [OrderID]; COUNT WHERE [FPID]`, 462-468) and, when no split rows remain or the table was not split, `UPDATE [FloorPlan] SET [Status]=6, [LastAct]=GETDATE(), [OrderID]=NULL WHERE [FPID]` (474-478) plus `OnUpdate` → `frmRegister._OnUpdate` (9477-9481, `UpdateFP` + socket broadcast). The dialog returns `Abort` → frmRegister `Clear()` (14038-14042) nulls `InUseID` of **whatever order the register had loaded**, not the voided one.

### 6.4 frmRefund Void of a completed order (frmRefund.cs:1875-3034)

Preconditions: `mCanVoid` = original `RefundAmt==0` (469), no Debit/EBT payment (566-580), no card payment with `CCTxs.BatchClose` set (597), no non-refundable Certificate/GiftCard/Club line (694-714); enabled only when `_Ref==_Total` (1544); card payments must be from this terminal (1913-1914); every row gets `RefAmt=Amount` (1916); `ReasonID` prompt only when `RefReqReason` else 40 (1951) — and **the reason is never persisted** (write 13 has no ReasonID/Note). Per card payment (Credit/Debit/EBT): `Global.Processors.VoidAuth(PayID, true, true)` (2250, TxType Void; its batch does CCTxs 3/6, Payments 9/`CloseDate`, `OrdersFPSplit.Status=3`, `Orders.TabAuthID=NULL` for tabs; Orders/OrdersTips skipped). Cash/Certificate/eGift/OnAccount accumulate for the drawer/certificate/CRM; types with no `case` (CardOnFile, DeliveryService, ClubDiscount, PayIn/PayOut, Offline in Void mode) get no processor call. Then: stock `UPDATE [Items] SET [QtyOnHand]=[QtyOnHand]+@QtyToRefund WHERE [ItemID] AND [StockType]<>2` per loaded line (2648-2654, raw, Refund flag ignored, `QtySold` untouched); Certificate/GiftCard lines → `GiftCards SET Balance=0, Used=1, UsedDate` (GiftCardManager.cs:149-167 via 2662) and provider `Redeem`; **no** refund order, no OrdersDetails/OrdersTax/OrdersTips/OrdersLog change. The original: `UPDATE [Orders] SET [Status]=5, [RefundAmt]=ISNULL([RefundAmt],0)+_Ref, [VoidDate]=GETDATE() WHERE [OrderID]` (2706-2733; `CloseDate/ReasonID/Note` untouched; the FloorPlan CleanUp at 2749 is dead — `@FPID` is never assigned). Per voided payment: `UPDATE [Payments] SET [RefundAmt]=ISNULL([RefundAmt],0)+@RefAmt, [Status]=9 WHERE [PayID]` (2817-2819) with `UPDATE [CCTxs] SET [RAmt]=@RefAmt, [Status]=3 [, Number=NULL, OffLineStatus=4 for Offline] WHERE [CCTxID]` (2823-2832); whenever that batch returns no row (every non-offline row) `UPDATE [Terminals] SET [OffDate]=NULL, [OffSent]=NULL` (2846-2850). `mCert>0` → a new `GiftCards` certificate (`FromRefund=1`, GiftCardManager.cs:370-402 via 2906-2913, `OrderID`=the original for Void). Tips and `Paid` are left alone. Success popup (3018-3023); then either `_Clear()` → `UPDATE [Orders] SET [InUseID]=NULL` (222, 3033) or, with **Re-create** checked, writes 20-27 of section 7.5 and `Global.Register.SearchOrder(newID)` — the original's `InUseID` is then **never released** (no FormClosing handler).

### 6.5 Row-level comparison

| Effect | VoidOrder (6.1) | VoidAuth `pVoidOrder=false` (6.2) | Recall-list Void (6.3) | frmRefund Void (6.4) |
|---|---|---|---|---|
| `Orders.Status` | 1 → 5 | 1/6 → 0 or 6 (only that payment) | 0 → 5 | 1 → 5 |
| `Orders.RefundAmt` / `VoidDate` / `CloseDate` / `ReasonID` | `=Paid` / now / — / — | — | — / — / GETDATE() / 40 or reason | `+=_Ref` / GETDATE() / — / — |
| `Orders.Paid` | unchanged | `-=Amount` | unchanged | unchanged |
| `Payments.Status` | 1/6 → 9, `RefundAmt=Amount`, `CloseDate` | → 9, `CloseDate`, tab `Amount=Value` | 0 → 14 | 1/6 → 9, `RefundAmt+=Amount` (CloseDate via VoidAuth for cards) |
| `CCTxs` | `Status=3, RAmt, Tip=0` | `Status=3, TxType=6, Tip=0` | untouched | `Status=3, TxType=6, Tip=0` then `RAmt` |
| `OrdersTips` | untouched | that PayID's rows deleted; `Orders.Tip/Total -= CCTxs.Tip` | untouched | untouched |
| `Items.QtyOnHand` | `+= Qty` all lines incl. pseudo-items, no StockType filter | — | `+= SUM(Qty)` non-deleted, `StockType<>2` | `+= QtyToRefund` per line, `StockType<>2` |
| `OrdersLog` | none | none | (6, UserID), (2, MgrID) | none |
| FloorPlan / OrdersFPSplit | `Status=6` (OrderID kept) | `OrdersFPSplit.Status=3` | `OrdersFPSplit` row deleted; `Status=6, OrderID=NULL` | none (dead) |
| Terminals | `OffDate/OffSent=NULL` when no offline rows remain | `OffDate/OffSent=NULL` always (online) | — | `OffDate/OffSent=NULL` per non-offline row |
| GiftCards | certificate re-issued for Certificate/Reward tenders | frmComplete: certificate lines cancelled | — | cert/card lines cancelled; certificate issued for `mCert` |
| Processor | Void per card (or local-only) | Void / AddValue | none | Void per card |
| Atomic | yes | no (one batch) | no (one batch) | no |

## 7. Refunding: `frmRefund`

### 7.1 Opening, loading, locking

Register → Refund (`btnRefundClick` 19277-19289: `_CheckForSave()`, `Clear()` releases the register's order, `Lib.ShowForm(typeof(frmRefund))` non-modal, no order id passed); scanned `'G'` barcode on the main form (frmMain.cs:2598-2608); Delivery → Manage (`new frmRefund(true)` modal, frmManage.cs:28-35). **Find Order** (`frmSearch(true)`) is the only place eligibility is filtered: `[O].[Status] IN (1, 7, 6) AND [O].[Paid] > ISNULL([O].[RefundAmt],0)` plus `DATEDIFF(DAY, ISNULL(CloseDate,CDate), @Today) <= @RefDays` when `Refund.RefDays<>0` (frmSearch.cs:69-81). `_LoadOrder` (frmRefund.cs:315-826) applies **no Status filter**; it rejects: missing order (354); `InUseID` held by another terminal (365-375, returns **without** `_Clear()`, leaving `MainID` set); `Paid<=0` (382); no `Completed=1`/`Auth=6` payment (474-522); a used certificate line (678-684). The RefDays check at 394-401 never fires for past orders. Payments are read with `Amount = ROUND([Amount]-ISNULL([RefundAmt],0),2)` (remaining refundable); Coupon/Discount rows hidden; Check/CheckCashBack/PayOutCust re-typed to Cash in memory (556-607); `mCanVoid` cleared for Debit/EBT or any batch-closed card; `_CanRefund=false` if any payment is Offline=11 or Auth=6 (605-606). Details: `[Qty]-ISNULL([RefQty],0) AS QtyToRefund WHERE [Deleted]=0 AND [Qty]>ISNULL([RefQty],0)` (638-649) — fully refunded lines disappear. Then the lock: `UPDATE [Orders] SET [InUseID]=@InUseID (Global.TerminalID) WHERE [OrderID]` only if it was NULL (722-729, raw). `_Remain = Paid − RefundAmt` (467). Clear/Cancel/Find-Order-OK/`LoadOrder` re-entry run `UPDATE [Orders] SET [InUseID]=NULL WHERE [OrderID]=MainID` (222/1571) — with whatever `MainID` holds, so after a refused load they can wipe **another terminal's** lock. No `FormClosing` handler exists: closing the form any other way leaves `InUseID` stale.

### 7.2 Refund mode: validation and money movement (1890-2176)

`btnRefund.Enabled = _CanRefund && _Ref<>0 && _Paid>=_Ref && !chkReCreate.Checked` (1543, 3084); security `Register.Refund` (858). Reason prompt only when not Pending and `RefReqReason`, else `_ReasonID=39` (1951). Checked GiftCard lines pass `GiftCardManager.EnsureReturnability` (1924-1927, HTTP). The amount is auto-allocated FIFO over the payment view (1992-2009) or `frmRefundTool` takes per-payment `RefAmt` / rewrites every visible payment's Type to Certificate=12 ("Gift Cert") or eGift=7 ("Gift Card" — calls provider `Activate(Redeem=_Ref)` **immediately**, 2044). **Payment loop** (2178-2434): per visible payment with `RefAmt<>0`, `RefTD/RefTF/RefSurcharge` computed only for `Type<14` (2202-2228); `mVoidPayment = Void mode || Status==Auth=6` (2231). Card types (3/4/18/6): `VoidAuth(PayID,true,true)` (2250) when `mVoidPayment`, else `CardData{TxType=Return=5, Amount=RefAmt, OCCNumber, RefCode=PNRef, Surcharge, PayID=DBNull}` (2264-2277) — offline: manual card, fake AuthCode `'xxOxL'`, `SaveTrx` (2308: CCTxs `TxType=5, Status=1, TxRespons='Offline'`, no Payments update because `PayID` is DBNull) + batch `UPDATE [CCTxs] SET [Number]=RSA(...); UPDATE [Terminals] SET [OffDate]=GETDATE(),[OffSent]=NULL WHERE [OffDate] IS NULL` (2310-2323), row re-typed Offline; online: `Process` (2349, inserts the Return CCTxs row via SaveTrx, decline → `CCTxFailures`) then `UPDATE [CCTxs] SET [Tip]=-(tip)` for the matching selected tip row (2360-2367). Cash → `mCash`; Certificate/Reward → `mCert`; eGift → `mGift`; OnAccount → `mOnAccount`; Factor4 → provider refund (2375-2397). A processed row gets in-memory `Status` Voided=9 / Refunded=8 and joins `mPayRows` (2427-2431). **Money moves before any local row is written**; a `SqlException` in 2360-2367 offers Retry (a second processor Return) / Issue Certificate / Cancel (2406-2424).

### 7.3 Refund persistence: the refund order (2438-2634, Refund mode, original not Pending)

| # | file:line | table | write | exec |
|---|---|---|---|---|
| 3 | 2448-2481 | Orders (refund order) | INSERT: `CustomerID, CustomID, OtherID, RefID=MainID (NULL if _FromItem), ReasonID=_ReasonID, StaffID (_FromItem ? Global.UserID : original), TaxID, TerminalID=Global.TerminalID, BaseAmount=-Round(_RefBaseAmount,2), CloseDate=GETDATE(), DeliveryFee=+_DeliveryFee if chkDelivery else NULL (not negated), Note, OrderType=_OrderType, OtherAmount=-_OtherAmount if chkOther else NULL, Paid=-_Ref, Status=Refunded=4, Tax=-_RefTax, TDUsed=-…, TFUsed=-…, Tip=-_RefTip, Total=-_Ref, Surcharge=**+_RefSurcharge** (double negation: the VALUES list uses `-@Surcharge` and the parameter is bound as `-_RefSurcharge`, 2451/2466 — the only money column of the refund order that keeps the original's sign); SELECT SCOPE_IDENTITY()` → `mOrderID` | wrapper ExecScalar |
| 4 | 2484-2507 | OrdersLog ×3 (+OrdersDelivery) | `(mOrderID, Global.UserID, Create=0)`, `(…, Close=1)`, `(mOrderID, Global.MgrID, Manager=2)`; Delivery orders copy the `OrdersDelivery` row | raw batch |
| 5 | 2514-2522 | OrdersTax | per slot with a value: `Qty=-_RefValue[i], QtyExempt=-_RefExValue[i], Type=i, CDate=DateTime.Now` | raw per slot |
| 6 | 2530-2548 | OrdersTips | per selected tip when `_RefTip<>0`: `UPDATE SET [RefAmount]=[Amount] WHERE [TipID]`; `INSERT (OrderID=mOrderID, StaffID, Amount=-Amount, Type)` | raw batch per tip |
| 7 | 2562 | Items | `QtyOnHand += QtyToRefund WHERE ItemID AND StockType<>2` (`QtySold` untouched) | same CommandText as 8 or 9+10 (raw, 2612) |
| 8 | 2566-2570 | OrdersDetails (refund order) | `_FromItem` only: 44-column INSERT…SELECT from `Items JOIN ItemsSKUs`, `Qty=-QtyToRefund, RefQty=+QtyToRefund`, prices from the grid | with 7 |
| 9 | 2572-2574 | OrdersDetails (original line) | `UPDATE SET [RefQty]=ISNULL([RefQty],0)+@QtyToRefund WHERE [ODID]` | with 7, 10 |
| 10 | 2576-2580 | OrdersDetails (refund order) | 65-column copy of the ODID with `OrderID=mOrderID, Qty=-QtyToRefund, Modifier=IIF(DRV IS NULL OR DRV=0, Modifier, 0)`, `RefQty` NULL, original `StaffID/UserID` | with 7, 9 |
| G0 | GiftCardManager.cs:149-167 via 2616 | GiftCards | `Balance=0, Used=1, UsedDate` for a refunded Certificate=5/GiftCard=6 line (`GiftCode`=SKU); provider `Redeem` for eGift | Dapper |

Pending originals skip all of this (`mOrderID = MainID`, 2440-2441).

### 7.4 Payments and the original order (2684-2854), post-processing (2856-2947)

| # | file:line | table | write | condition |
|---|---|---|---|---|
| 12 | 2688-2700 | Payments (refund order) | INSERT `BankID, CloseUserID=UserID=Global.UserID, OrderID=RefID=mOrderID, TerminalID, Amount=-_Ref, CloseDate=GETDATE(), Status=Refunded=8, Type = btnRefund ? eGift=7 : Certificate=12` OUTPUT PayID | `_FromItem` (refund without receipt) |
| 13 | 2706-2733 | Orders (original) | `UPDATE SET [Status]=@Status, … WHERE [OrderID]`: Refund → Pending ? (`_Ref==_Remain` ? Open=0 : Pending=6) : Completed=1; Void → Voided=5; Pending: `Paid=Paid-_Ref`, else `RefundAmt=ISNULL(RefundAmt,0)+_Ref`; Void adds `VoidDate=GETDATE()`. `ReasonID/Note/CloseDate` untouched | not `_FromItem` (raw; the Output parameters `@CustomerID/@FPID` are never assigned) |
| 14 | 2749-2753 | FloorPlan | `Status=6 WHERE FPID=@FPID` | **dead** (`@FPID` NULL) |
| 15 | 2768-2770 | Payments (original) | `RefundAmt=ISNULL(RefundAmt,0)+@RefAmt` (Status unchanged) | per `mPayRows` row with in-memory Status Refunded=8 |
| 16 | 2774-2776 | Payments (refund order) | INSERT `BankID; CCTxID = new Return tx (cards) / original CCTxID (non-card) / NULL (cash); CloseUserID=UserID=Global.UserID; OrderID=RefID=mOrderID; TerminalID; Amount=-RefAmt; CloseDate=GETDATE(); DeliveryFee=-Round(DeliveryFee×RefAmt/OriginalAmt,2) or 0; Status=8; TDUsed=RefTD, TFUsed=RefTF (negative), Surcharge=RefSurcharge (NULL for Type>=14); Tip = NULL if mTip==0 else -min(RefAmt, remaining mTip) FIFO (2790-2805); Type=row Type` OUTPUT PayID | as 15, original not Pending |
| 17 | 2817-2819 | Payments (original) | `RefundAmt=ISNULL(RefundAmt,0)+@RefAmt, Status=Voided=9` | per row with in-memory Status Voided=9 (Void mode, or an Auth=6 payment in Refund mode) |
| 18 | 2823-2832 | CCTxs (original) | `RAmt=@RefAmt, Status=3` (+`Number=NULL, OffLineStatus=4` for Offline) | as 17 with a CCTxID |
| 19 | 2846-2850 | Terminals | `OffDate=NULL, OffSent=NULL`; `NoOffline(false)` | whenever 17/18 returned no row (every non-offline voided row) |
| G1 | csPayment.cs:94-118 via 2894 | CCTxs; Payments | gift CCTxs row (`AType=GiftCard, CardHolder='eGift', TxType=Return=5, Status=1, AuthCode=gift Reference`), then `Payments SET CCTxID, Reference WHERE PayID=mPayID` | `mGift<>0` and a target card was requested and `Activate` succeeded |
| G2 | GiftCardManager.cs:129-142 via 2895 | GiftCards | `Balance += mGift` on the target card | as G1, local row exists |
| G3 | GiftCardManager.cs:370-402 via 2906-2913 | GiftCards | INSERT certificate `Amt=Balance=mCert, FromRefund=1, Used=0, CustomerID=Lib.ToInt(_CustomerID) (0 when none), OrderID=mOrderID` | `mCert>0` |
| — | 2918-2929 | (CRM) | `AccountReceivableManager.CreateTransaction` RETURN, `ParentRefId=MainID` | OnAccount refunded (cloud only) |
| 28 | 222-226 via 3033 | Orders (original) | `InUseID=NULL` | end of handler without Re-create |

Drawer when `OpenAlwaysCD` or `mCash<>0` (2934), CFD "Returned amount", `Print.Refund(mOrderID)` on a thread (the refund order for Refund, the original for Void; nothing for Pending), certificate print.

### 7.5 Re-create after Void (2955-3016)

`INSERT INTO [Orders] … SELECT` 21 header columns from MainID (incl. `InUseID`=this terminal, `Note, Tax, Tip, Total, DeliveryFee`) with `StaffID=Global.UserID, TerminalID=Global.TerminalID, Status=Open=0`, `Paid/RefID/ReasonID/FPID/CloseDate/Surcharge/RefundAmt/VoidDate` NULL, `OUTPUT INSERTED.[OrderID]` (2961-2972, wrapper) → then one raw batch (3014): `OrdersLog (new, Global.UserID, Create=0)` (2975, `@Type=OrderLogType.Create` 3013); `OrdersTax` copy with `CDate=GETDATE()` (2978); `Payments` copy of **ClubDiscount=15 rows only** with `Status=Open=0` (2983); `OrdersDelivery (new, Global.UserID, New=0)` for Delivery orders (2989); `OrdersTips` copy with `StaffID=IIF(StaffID=_StaffID, Global.UserID, StaffID)`, `Type/PayID` NULL (2994); `OrdersDetails` 67-column copy `WHERE ISNULL(Deleted,0)=0`, `Qty` positive, `RefQty` NULL (2999); `UPDATE [Items] SET [QtyOnHand]-=Qty, [QtySold]+=Qty` from the new order's lines `WHERE StockType<>2` (3004 — re-deducts what the void restored). Then `Global.Register.SearchOrder(newID)` + `Close()` (3029-3030): the register's `_LoadOrder` skips its own lock (`InUseID` already set) and the **original stays locked**.

### 7.6 Open-ended refund (`btnOpenClick` 1603-1873, `Refund.RefOpenEnded`, no security access)

Amount (`frmMessageValue`, `MaxValue=RefOpenEndedAmount`), tender (`frmPayType`: Cash/Credit/Check/Debit/Certificate/eGift as configured), confirmation, optional reason (`frmNotes` Type 14, else `ReasonID=39`). Credit/Debit: `CardData{OrderID=0, PayID=DBNull, TxType=Return=5}`; offline Credit → `Offline=11`, `Offline(true)`, manual card, fake AuthCode, `SaveTrx` (1705) + `UPDATE [CCTxs] SET [Number]; UPDATE [Terminals] SET [OffDate]=GETDATE(),[OffSent]=NULL WHERE [OffDate] IS NULL` (1706-1719); offline Debit/EBT falls through with a warning and **still writes the rows** (1686-1687, bug); online → `Process` (1736; failure → certificate fallback or abort). Cash opens the drawer when `OpenAlwaysCD`. Certificate → `mCert=amount`. eGift → provider `Activate` (1794), `IssueCertificate` re-routes to Certificate. Then: `INSERT INTO [Orders] (CustomerID=NULL, ReasonID, StaffID=Global.UserID, TaxID, TerminalID, BaseAmount=-@Amount, CloseDate=GETDATE(), Note, OrderType=0, Paid=-@Amount, Status=OpenRefund=10, Tax=0, TDUsed=0, TFUsed=0, Total=-@Amount) OUTPUT INSERTED.[OrderID]` → `mRefID` (1807-1835, wrapper) and one batch via `ExecNonQuery` (1839-1854): `INSERT [Payments] (BankID, CCTxID, CloseUserID=UserID=Global.UserID, OrderID=@RefID, TerminalID, Amount=-@Amount, CloseDate=GETDATE(), Status=OpenRefunded=13, Type)`; `OrdersLog (RefID, UserID, 0)`, `(RefID, UserID, 1)`, `(RefID, MgrID, 2)`; `INSERT [OrdersDetails] (CatID=1, ItemID=Refund=1, OrderID, SubCatID=1, Description='Open Ended Refund', Price=PriceBase=PriceDisplay=PriceOriginal=PriceSell=+@Amount, Qty=-1, RefQty=1)`. Then `Print.Refund(mRefID)` (email mode may `INSERT [EmailsSent]`, Lib.cs:3233, on a thread), `CreateGiftCertificate` + `Print.GiftCert` when `mCert>0` (1859-1869, `CustomerID=NULL, FromRefund=1`), "Refund completed.". No `OrdersTax`/`OrdersTips`, no existing order touched.

### 7.7 Row summary for the four refund shapes

| Shape | New `Orders` row | Original `Orders` | Original `Payments` | New `Payments` | `OrdersDetails` | Stock |
|---|---|---|---|---|---|---|
| Full refund (Completed original) | `Status=4, RefID=orig, Total=Paid=-Paid, CloseDate` | `Status=1` rewritten, `RefundAmt=Paid` | `RefundAmt=Amount`, Status 1 kept (Auth rows → 9) | one per tender, `Status=8, Amount=-RefAmt, OrderID=RefID=new`, cards linked to a Return CCTxs | original lines `RefQty=Qty`; negative mirrors on the refund order | `+QtyToRefund` (StockType<>2) |
| Partial refund | same, amounts = refunded part | `RefundAmt += _Ref` | `RefundAmt += RefAmt` | `Amount=-RefAmt` | `RefQty += n`; mirror `Qty=-n` | `+n` |
| Refund of a Pending original | none (`mOrderID=MainID`) | `Paid -= _Ref`, `Status` 0 if fully reversed else 6 | `RefundAmt += RefAmt` | none | untouched | untouched |
| Void | none | `Status=5, RefundAmt=Paid, VoidDate` | `Status=9, RefundAmt=Amount` (+ VoidAuth batch for cards) | none | untouched | `+Qty` every line |
| Open-ended | `Status=10, Paid=Total=-Amount` | — | — | `Status=13, Amount=-Amount` | one `ItemID=1` line `Qty=-1` | none |

## 8. Tips

Tips live in three places that are written independently: `Orders.Tip` (the order-level total, rewritten to `_TipAmt` at close), `Payments.Tip` (the share allocated to a tender) and `OrdersTips` rows (one per tip event, typed by `TipType`). They do not have to agree: `Orders.Tip` at close is the whole `_TipAmt` and may exceed `SUM(Payments.Tip)` (18652); `spTipAdjust` replaces `Orders.Tip` while inserting yet another `OrdersTips` row.

| Path | Trigger | `OrdersTips` | `Payments` | `Orders` | Where |
|---|---|---|---|---|---|
| Register tip (Add Tip / group tip / percent) | `btnAddTipClick` → `frmTips` → `btnSaveClick`; `btnPercentClick` | INSERT `(OrderID, StaffID, Amount, Type ∈ {Group=2, Amount=3, Percent=4}, Value)` OUTPUT TipID, `PayID` NULL; UPDATE by TipID; DELETE by TipID for `Deleted` rows | — | `Tip=_TipAmt`, `Total` includes `Round(_TipAmt,2)` (12540) at every save | 2702-2737; 16243-16265 |
| Tip share per tender at pay | batch A | — | `Tip` = pro-rated share of `(_TipAmt-_TipPaid)` or NULL; `FeeFromTips=Tip×TenderFee.Amount` for Credit/Debit with `DeductFeeFromTips` | `Tip=_TipAmt` at close | 17994-18017, 18008; 18652 |
| CFD tip and signature (multitouch, before the card is read) | `Global.CFD.TipAndSignature` OK with tip ≠ 0 | INSERT `Type=CFD=7`, `PayID`=row PayID, OUTPUT TipID → `mValues.Object4` | — | `Total += tip` (DB always; memory only when no split payments) | 16794-16798 |
| Terminal (PIN-pad) tip, non-multitouch, `Account.TipReq` | after a non-tab card approval with `CardData.Tip<>0` | INSERT `Type=Terminal=5`, `PayID` | row `Amount += tip` (memory, then batch A) | `Total += tip`; `CCTxs.Tip=NULL` | 17546-17594 |
| Tab Complete / Complete Auth / delivery credit (`csPayment.Complete(pID, pTip)`) | 16915; `_OnComplete` 9324; frmDelManager.cs:659 | INSERT `(OrderID, PayID, StaffID=Orders.StaffID, Amount=tip, Type = Global.TouchScreen ? CFD=7 : CC=1, Value=tip)` when no row for that PayID (547), else UPDATE `Amount, Type, Value` (553) — tip ≠ 0 only; the `TipType.Gift=6` branch of that expression (560) is unreachable because the gift path never sets `mCardData.AccountType` (only the card branch does, 432/438), so eGift auth completions also get 1/7 | `pTip==null` (or boxed Int32 -1): `Amount += tip, Status=1, CloseUserID=Orders.StaffID` (495-500); **plus** `Amount += tip` again when `Global.TouchScreen` (565-569); `FeeFromTips` (575-579) | `pTip==null`: `Paid += tip, Tip += tip, Total += tip` (502-506); surcharge (527) | csPayment.cs:293-588 |
| CFD tip then card declined | `WithError` | `OrderID=-OrderID` on the CFD tip row (19040) | row soft-deleted | `Total` reverted **only for eGift** (19028) | 19012-19040 |
| Tip adjust after close (`frmReView`, Register → Complete → Review) | tip changed, `CCTxs.Partial=0`; Credit → processor Adjustment, eGift → `CompleteAuth` | old `CCTxs.Tip` NULL → INSERT `Type=CC=1, StaffID=Orders.StaffID`; new tip 0 → DELETE every row for the PayID; else UPDATE `Amount/Value += Δ` | `Amount += Δ` (`Payments.Tip` never touched) | `Paid += Δ, Tip=ISNULL(Tip,0)+Δ, Total += Δ`; `CCTxs.TransmitID, TxType=Adjustment=4` (151), `CCTxs.Tip = new or NULL` OUTPUT DELETED.Tip | frmReView.cs:151-190 |
| Delivery manager cash close | Complete with `frmComplete.Tip` (prefilled from `Orders.Tip`) | INSERT `(OrderID, PayID=new, StaffID=driver, Amount, Type=Amount=3, Value)` when tip ≠ 0 | new row `Tip=IIF(@Tip=0,NULL,@Tip)`, `Amount=(Total-Paid)+Tip` | `Paid=Total+Tip, Tip=@Tip, Total += Tip` (existing tip counted twice — open question) | frmDelManager.cs:606-654 |
| Delivery manager credit close | `Complete(PayID, tip)` (decimal → rows 495-509 skipped), then batch H | as `Complete` above, `StaffID` = the driver just written by `SetDeliveryStatus` | `Amount += Tip, CloseUserID=Global.UserID, Status=1` (batch H; TouchScreen adds the tip twice with 565) | `Tip += Tip, Paid += Tip, Total += Tip` (batch H) | frmDelManager.cs:663-681 |
| Pay-at-table device (`spApplyPayment`) | external Clover Flex service | INSERT `(OrderID, PayID=new, StaffID=Orders.StaffID, Amount=@tip, Type=Terminal=5, Value=@tip)` — row even for tip 0 | INSERT `Type=3 (always), Status=1, Tip=@tip, MachName='Clover Flex'` | `Paid += total, Total += tip, Tip += tip, PaidAtTable=1, Status/CloseDate` | db-procs.txt:58-72 (explicit transaction) |
| Pay-at-table adjust (`spTipAdjust`) | external | INSERT another `Type=5` row per call | `Amount=@total, Tip=@tip` (replaced) | `Paid += tip, Tip=@tip (replaced), Total += tip` | db-procs.txt:297-306 (transaction) |
| Refund | selected tips with `_RefTip<>0` | `RefAmount=Amount` on the original row; mirror `Amount=-Amount` on the refund order | refund row `Tip=-min(RefAmt, remaining)` FIFO; card Return `CCTxs.Tip=-tip` | refund order `Tip=-_RefTip` | frmRefund.cs:2530-2548, 2790-2805, 2360-2367 |
| Void of one payment (`VoidAuth`, `pVoidOrder=false`) | frmComplete/frmReView Void | DELETE `WHERE PayID` | `CCTxs.Tip=0` | `Tip -= CCTxs.Tip, Total -= CCTxs.Tip` | csPayment.cs:1108, 1138-1142 |
| Offline declined settlement | frmOfflineProcess | DELETE `WHERE OrderID AND PayID AND Type=CC=1` | — | `Tip -= CCTxs.Tip, Total -= same` | frmOfflineProcess.cs:200-204 |
| Whole-order voids | `OrderManager.VoidOrder`, frmRefund Void, recall-list Void | untouched | — | untouched | 6.x |
| Split | `btnSplitClick`, after the "tips will be removed" confirmation | non-group tips deleted; group tip `Amount` rewritten for the original, new `Type=2` row per child with `StaffID`=original's | tab row `Amount` rewritten | per sub-order `Tip=_TipAmt` | 19644, 19784-19795 |
| Reassign / Take / delivery driver change | — | `StaffID` rewritten on **every** tip row of the order (19234/19246); `OrdersTips.StaffID=new WHERE StaffID=old` when `Orders.Tip<>0` (Lib.cs:6971) | — | `StaffID` | 19234-19259; Lib.cs:6947-6975 |

`FraudControl.TipSecurity` caps: a `Type=Amount=3` tip that exceeds `(_SubTotal − QtyDel×PriceSell)×TipSecurity` after a qty reduction is flagged `Deleted` in memory (12753-12767); `Complete`'s prompt and `frmReView` refuse tips over the cap (csPayment.cs:353-374; frmReView.cs:100-108).

## 9. Other mutations

### 9.1 Pay In / Pay Out (`btnPayInOutClick` 16124-16178)

Enabled only on an empty cart. `frmPayInOut` needs a `CustomFields` reason row (`Type` 16 PayIn / 17 PayOut, Active=1), a note and an amount > 0; `Amount = ToDec(txtAmount) × (In ? 1 : -1)`. Round trip 1 (16135-16146, raw ExecuteScalar): `INSERT INTO [Orders] (StaffID=Global.UserID, TaxID=Settings.Tax.TaxID, TerminalID=Global.TerminalID, CloseDate=GETDATE(), OrderType=PayIn=6/PayOut=7, Status=Completed=1, Tax=0, Total=0, RefID=CustomFields.CustomID or NULL) OUTPUT INSERTED.[OrderID]` — `Paid`, `CustomerID`, `FPID` NULL. Round trip 2 (16148-16171, raw, one batch; parameters from command 1 are reused): `INSERT [OrdersLog] (OrderID, Global.UserID, Create=0)`, `(…, Close=1)`; `INSERT [Payments] (BankID=LocalSetting.BankID or NULL, OrderID, RefID=CustomID or NULL, TerminalID, UserID, Amount=signed, CloseDate=GETDATE(), Reference=note, Status=Completed=1, Type=PayIn=8/PayOut=9)`; and, only when `Global.LocalSetting.BankID != null` (16155), `INSERT [BankMod] (BankID, UserID=Global.UserID, MngrID=Global.MgrID, Cash=signed amount, Date=GETDATE(), Notes=note)`. Then `Lib.OpenCashDrawer()`, receipt, `Clear()` (a DB no-op because `MainID` is DBNull). No `OrdersDetails`, no security gate. If round trip 2 throws, a Completed Pay In/Out order with no payment remains.

### 9.2 Split by item (`btnSplitClick` 19547-19853)

Confirmations: non-group tips will be removed (19554), promotions may be removed (19566); `frmSplitItem` assigns `Split` 1..5 to moved lines and creates share rows (`ItemID=ShareItem=9`/`ShareService=11`, `Shared=1`, ODID NULL). Sequence: `SaveOrder(sender)` (19597 — an unsaved order is INSERTed; share rows get ODIDs on the **original**); `DELETE FROM [OrdersTax] WHERE [OrderID]=MainID` [+ `DELETE [OrdersFPSplit]`] (19621-19633); `DELETE FROM [OrdersTips] WHERE [TipID]` per non-group tip (19643-19647). Six passes (19655-19800): pass 0 = `[Split] IS NULL AND [Deleted]=0` (stays on the original), passes 1..5 = moved groups; each pass reloads `_OrderDetails` with its subset and re-runs `_TotalUpdate`. Child passes: `INSERT INTO [Orders] (CustomerID, CustomID, FPID, OtherID, RefID, StaffID, TaxID, TerminalID, Label, LegalAge, LegalAgeMethod, LicenseName, Loyalty, Note, OrderType, Printed, Status, SplitPayments) SELECT … FROM [Orders] WHERE [OrderID]=MainID; SELECT SCOPE_IDENTITY()` (19615-19619, run 19676, wrapper) — `Paid, InUseID, Exchange, CustExempt, Surcharge, TDUsed, TFUsed` NULL, `Status` copied (normally 0); then `INSERT [OrdersLog] (@ID=Global.UserID, @OrderID=child, @Type=OrderLogType.Create=0)` (19679-19694) [+ `OrdersDelivery` copy for Delivery/DeliveryService/PickUp types] (raw batch 19695). Pass 0 with a tab auth: `UPDATE [Payments] SET [Amount]=_Total WHERE [PayID]=_TabAuthID` (19700-19706, no processor call). Every pass: `INSERT [OrdersTax] (OrderID=sub-order, Qty, QtyExempt, Type=j, CDate=DateTime.Now)` per qualifying slot (19710-19721); `UPDATE [OrdersDetails] SET [OrderID]=sub-order, [PriceSell], [Printed], [PromoUnit], [PromoLink], [PromoQty] WHERE [ODID]` per line — **the move**, `CDate` untouched (19730-19741); [`INSERT [OrdersFPSplit] (FPID=_CurrentObj.ID, OrderID=sub-order, Status=Ordered=3 or TabAuth=7 for pass 0 with a tab)` +] `UPDATE [Orders] SET [BaseAmount],[Credit],[DeliveryFee],[ItemTotal],[Loyalty],[OtherAmount],[Tax],[Tip]=_TipAmt,[Total] [,Printed=1] OUTPUT INSERTED.[StaffID] WHERE [OrderID]=sub-order` (19743-19779; `@Exchange` bound but not in SET — the original's `Exchange` goes stale); group tip: `UPDATE [OrdersTips] SET [Amount]=_TipAmt WHERE [OrderID]=MainID` (pass 0) / `INSERT [OrdersTips] (OrderID=child, StaffID=OUTPUT StaffID, Amount, Type=Group=2, Value)` (19781-19795). Then `OrderDetailsAsync` per sub-order when print-orders/RemoteOnSave (csPrint.cs:1922 `Printed/Fire`), floor plan `_CurrentObj.Status=Split=10; DBUpdate()` (19820-19821: `OrderID=MainID, InUse=0, Status=10, LastAct`), `frmFPOrder` picker → `_LoadOrder` or `Clear()`; non-floor-plan → `Clear()` + `btnGetOrderClick` (19847-19848).

### 9.3 Exchange, remove line, clear all, tax exempt, age check (in memory until the next `SaveOrder`)

Exchange (`btnExchangeClick` 13807-13932): an `ItemID=NotPrinted=19` "** Exchange Item **" row is inserted above the line, the line gets `Qty=-q` (13858-13887); `_TotalUpdate` sums exchange lines into `_Exchange` → `Orders.Exchange` (12414-12415 → 2030/2104); the negative tender is committed with `Payments.Status=Refunded=8` and does not touch `Orders.Paid` (17960). Undo removes the label row without queueing its ODID (13874-13877, open question). Remove line (`btnDeleteClick` 13482-13645): flags `Deleted`, `DelUserID`, queues `csDelRow` (13542-13545) — persisted by SaveOrder as the soft delete + stock restore (3.8); deleting the last line sets `Modified=false` and skips `Clear()` (13629-13635), so the queued deletes are **not** persisted until something else saves and the lock stays (open question). Clear All (`btnClearClick` 13389): "Save Order" → `btnSaveClick(_CmdSave)`; "Clear All" → `Log` row `NewValue='Clear All'` (AuditManager, 13416) then `Clear()`. Tax exempt toggles `OrdersDetails.TaxExempt` (19866-19922, persisted 2430/2462; feeds `OrdersTax.QtyExempt`); age check sets `LegalAge/LegalAgeMethod/LicenseName` (13205-13213 → 1975-1977).

### 9.4 Join and Move (Lib.JoinOrder Lib.cs:4902-5116; floor plan 14706-15281; frmGetOrders Join 231-312)

**Join** (grid Join button, or floor-plan Join of two non-split tables): per source, optional `UPDATE [Payments] SET [Status]=9 WHERE [OrderID]=source AND [Type]=15` (4944, raw) then one batch via `ExecScalar` (4953-5023): `OrdersTax` types the target lacks re-parented, shared types summed into the target, source tax rows blanked (`Qty/QtyExempt/Type=NULL`, not deleted); all `OrdersDetails` (Deleted included) → `OrderID=target, LinePos += MAX(target)+1` (4982); `OrdersPromos`, `Payments` (any status), `OrdersTips` re-parented (4987/4992/5009); target `BaseAmount, ItemTotal, Tax, Total += source`, `Paid/Tip/TDUsed/TFUsed` ISNULL-summed, `Status=IIF(T=S, T, Pending=6)` (4996-5000); source `StaffID=Global.UserID, Status=JoinIn=2, Total=0, Tax=0, CloseDate=GETDATE(), RefID=target OUTPUT FPID` (5013). Floor plan: `OrdersFPSplit` rows deleted (5031-5062), table set Ordered/CleanUp via `UpdateFP` + broadcast only — **`FloorPlan.OrderID` still points at the JoinIn source** afterwards. With a `TabAuthID` on the target: `UPDATE [Payments] SET [Amount]=Orders.Total-ISNULL(Paid,0) … JOIN Orders ON PayID=TabAuthID` **with no OrderID filter** (5082 — DB-wide); extra Group tips on the target deleted (5090-5111). frmGetOrders returns `DialogResult.Yes` unconditionally (312), so the register then runs `_CheckForSave`, `_LoadOrder(target)` (lock 8922), full `SaveOrder` when `OnJoinOrder` fired (14053) and `UPDATE [Orders] SET [Tax]=_Tax,[Total]=_Total` (14060) — the batch's summed totals are transient. Delivery-type sources are dropped silently (frmGetOrders.cs:252-256). Floor-plan Join with a TabAuth source first runs `VoidAuth(TabAuthID)` (14918: CCTxs 3, Payments 9, `Orders.TabAuthID=NULL`); if either table is Split the orders are only **re-parented** — `UPDATE [Orders] SET [FPID]=@FPID …; INSERT/UPDATE [OrdersFPSplit]` (14928/14942/14960) after the target is set `Split=10` (14972).

**Move** (floor plan, target must be `Open=1`): `UPDATE [Orders] SET [FPID]=@FPID` for the source order (14867) or, for a Split source, all its checks + `OrdersFPSplit.FPID` (14836); target table `OrderID=source order, Status=source Status, InUse=0, LastAct` (14880); source table `OrderID=NULL, Status=6` (14886). `Orders.StaffID/Status/InUseID` unchanged. Note the silent take-over on any in-use table click: `UPDATE [Orders] SET [StaffID]=Global.UserID` **before** the popup (14802/14808), not reverted by "None".

### 9.5 Reassign / Take (`btnReassignClick` 19165-19276)

Immediate, not deferred to Save. One batch: `UPDATE [Orders] SET [StaffID]=@StaffID …; UPDATE [OrdersTips] SET [StaffID]=@StaffID …` — `WHERE [OrderID]=MainID` (19246) or `JOIN OrdersFPSplit WHERE FPID` for every check of a Split table (19234); **all** tip rows, not only Group. Reassign picks from `Staff WHERE Active=1 AND SoftAccess=1`; Take uses `Global.UserID`. The form field `StaffID` is set before execution (19255), so a later `SaveOrder` of a **new** order inserts that StaffID and its Create-row `ID`, which can differ from `Payments.UserID=Global.UserID` (db-plumbing open question 4 — by design after Reassign). Floor-plan Take falls through to `_LoadOrder` (15205); Reassign releases the table (19269).

### 9.6 Recall and the in-use lock (`_LoadOrder` 8520-8953, `Clear` 732-1059)

`_LoadOrder` first calls `Clear()` (8522: `UPDATE [Orders] SET [InUseID]=NULL` for the previously loaded order, 739; FloorPlan `InUse=0` for `_CurrentObj`/`_SelectedObj`, 1001/1012). It reads the Orders row, aborts on a foreign lock (`InUseID` not NULL and not this terminal; hard block 8557 unless `Global.SystemAdmin` confirms, 8546-8553) or `Status=Completed=1` (8565 — the lock check runs first, so a Completed order with a stale lock reports "Opened on terminal X"). With `Orders.FPID` it makes the table `_CurrentObj` with `InUse=true` and writes `FloorPlan SET OrderID=<object's existing OrderID>, InUse=1, Status=<unchanged>` (8674). Details, tips, tax, delivery, customer, payments (`Status<>9`) are loaded; totals recomputed in memory. The **last** statement is `UPDATE [Orders] SET [InUseID]=@InUseID (Global.TerminalID) WHERE [OrderID]` (8922-8925), **only if `InUseID` was NULL** at read time. Release: `Clear()` (Save 19407, auto-save 6399/6413, Fire Now 13973, foreign-tab exit 16543, completion 18950-18971, tab auth 17537, grid void 14042, refund button 19283, split 19838/19847, Clear All 13420) and `OnFormClosing` (2969) — `InUseID=NULL` regardless of Status. A killed terminal leaves a stale lock; `frmRefund` uses the same column with its own set/clear (7.1). frmGetOrders lists `Orders.Status IN (0,6)` (frmGetOrders.cs:114), own-staff-only when `OpenAllOrder > AllowLog` (119). Print Order (`_PrintOrder` 9920-9999): `UPDATE [Orders] SET [Printed]=1 OUTPUT INSERTED.[FPID],INSERTED.[TabAuthID]` (9933); tab-less table → `FloorPlan Status=Check=4` (9959 raw when the table is off-screen, guarded `Status<>10`; else `DBUpdate` 9971, first print writes `InUse=0, LastAct`); guest-check printing never touches `OrdersDetails.Printed`. Fire now from the floor plan: `UPDATE [OrdersDetails] SET [Fire]=1 WHERE [ODID]` per unprinted row (14001) then remote print (`Printed=Qty, Fire=0`, csPrint.cs:1922). Order-type change on a saved table order: batch `UPDATE [Orders] SET [FPID]=NULL; UPDATE [FloorPlan] SET [OrderID]=NULL,[InUse]=0,[LastAct]=NULL,[Status]=6 WHERE [OrderID]` (20055); the new `OrderType` itself is persisted only by `SaveOrder`. Reset All (19301-19345): CleanUp tables → `OrderID=NULL, InUse=0, LastAct=@Now (client clock), Status=Open=1` (19309-19325); other in-use tables → `InUse=0` (19330); Orders untouched. Floor-plan popup Available/Occupied: `DELETE [OrdersFPSplit] WHERE FPID` (15162/15179) and `FloorPlan OrderID=NULL, InUse=0, Status=1|2` (15190) — **the Orders row is not touched** (orphan link; the next recall re-attaches via `Orders.FPID`). The socket receiver (`UpdateFP`, 2900-2962) writes `FloorPlan Status=6, OrderID=NULL, InUse=0` when a frame's OrderID no longer exists or came from the LRS device (2946-2955 → csObject.cs:472).

### 9.7 Delivery manager (`frmDelManager`, Lib.SetDeliveryStatus Lib.cs:6901-7057)

Grid: `Orders.InUseID NULL`, `OrderType IN (3,5,2)`, `OrdersDelivery.Status<>Undeliverable`, Open/Pending or not delivered before today; Payments LEFT JOINed only for `TerminalID=this AND Status=Auth=6` (399-401). **Complete**: driver pre-assignment, `Delivery\frmComplete` (tip prefilled from `Orders.Tip`, disabled for online orders), then `_HandleRow` → `SetDeliveryStatus(Delivered)`: batch J `UPDATE [Staff] SET [DeliveryStatus] …; UPDATE [Orders] SET [StaffID]=@new OUTPUT DELETED.[StaffID]` (6942-6952), `UPDATE [OrdersDelivery] SET [StaffID]` (6955), `OrdersDetails/OrdersTips StaffID=new WHERE StaffID=old` when the driver changed and `Orders.Tip<>0` (6966-6975), old driver's status recomputed (7004), `UPDATE [OrdersDelivery] SET [Delivered]=@Now,[Out]=ISNULL([Out],@Now),[Ready]=ISNULL([Ready],@Now),[Status]=3` (7016-7021), pickup receipt, `SocketCode.Delivery=242` broadcast (7055). Then **cash/3PDS** (no Auth payment on this terminal): batch G (594-627): `INSERT [OrdersLog] (OrderID, ID=driver or NULL, Type=OrderLogType.Close=1)` (595, `@Type` bound at 643); `OpenPromoDetails` → `OrdersPromoDetails`; `UPDATE [Orders] SET [BatchID]=NULL,[CloseDate]=GETDATE(),[Paid]=[Total]+@Tip,[Status]=1,[StaffID]=@UserID,[Tip]=@Tip,[Total]=[Total]+@Tip` (606); `INSERT [Payments] (BankID, OrderID, TerminalID, UserID=driver, Amount=(Total-Paid)+Tip, DeliveryFee=driver share, RefundAmt=0, Status=1, Tip=IIF(@Tip=0,NULL,@Tip), Type = 3PDSID NULL ? Cash=0 : DeliveryService=16, Value=Amount, CloseDate=GETDATE()) OUTPUT PayID` (616); `UPDATE [Customers] SET [PrevOrder]=[LastOrder],[LastOrder]=@OrderID` (625); with tip ≠ 0 `INSERT [OrdersTips] (…, Type=Amount=3)` (651-654). No drawer, no receipt call. **Credit** (Auth=6 on this terminal): `_OnComplete` → `csPayment.Complete(PayID, tip)` (5.10) then batch H (663-681). **Undeliverable** (697-726): `SetDeliveryStatus` first (may reassign / promote a paid PickUp to Delivered), then `UPDATE [Orders] SET [Status]=Undeliverable=11; UPDATE [OrdersDelivery] SET [Status]=4` (706-717) and broadcast. Status buttons (Ready/Out) write `OrdersDelivery` timestamps + `Status=1/2` (7030/7042) and `Staff.DeliveryStatus`.

### 9.8 Dual price, online orders, time charges, floor-plan editor, frmMain upgrade

Dual-price writers are called only from `btnProcessClick` (5.4, 5.7): `UpdatePaymentRecordWithTFUsed` → `UPDATE [Payments] SET [TFUsed]=@TFUsed WHERE [PayID]` (csDualPriceManager.cs:278, no-op when 0 or PayID 0), `UpdateOrderTotal` → `UPDATE [Orders] SET [Total]` (301), and the completion rewrite of `Orders/OrdersDetails/OrdersTax/OpenPromoDetails/OrdersDelivery/Payments` (690-995). Online orders: a once-a-minute thread sets `UPDATE [Orders] SET [Printed]=1 WHERE [OrderID]` for `OnlineOrder=1, Printed=0` rows whose `OOTime` is due (frmMain.cs:3300-3347) and remote-prints them. Time charges: `frmTime` Start `INSERT [OrdersTimeCharge] (ODID, OrderID, StartTime=GETDATE())` (frmTime.cs:104), Stop `UPDATE … SET [EndTime]=GETDATE()` (111); completion deletes them (18623). Floor-plan editor OK (frmSetFloorMap.cs:589-662): `CustomFields.Image` (593), deleted tables → `DELETE [FloorPlan]; DELETE [OrdersFPSplit]; UPDATE [Orders] SET [FPID]=NULL WHERE [FPID]` (614-620, all history), new/updated tables (645/651, `Status=Open=1` for new), `Config.GlobalXML` rewritten (Lib.cs:7487). One-time upgrade `UPDATE [OrdersDetails] SET [PriceDisplay]` for roll-up parents (frmMain.cs:1361-1420).

### 9.9 Offline processing outcome (`frmOfflineProcess`, from `CloseBatch` in a non-DEBUG build)

Rows: `Payments.Type=11 JOIN CCTxs` on this terminal, `Number NOT NULL`, `Status<>3` (60-71). Each goes to `Default.ProcessAsync` (127-132). **Approved** → batch D: `UPDATE [CCTxs] SET [TID],[AuthCode],[CloseDate]=ISNULL([CloseDate],@Now),[OffLineStatus]=Approved=3,[PNRef],[Number]=NULL,[Status]=1,[TransmitID],[TxType]=(restaurant ? Capture=1 : Sale=2); UPDATE [Payments] SET [CloseUserID]=Global.UserID,[Status]=1,[Type]=Credit=3` (138-162). **Failed/timeout** → `UPDATE [CCTxs] SET [OffLineStatus]=Failed=1` (`Number` kept for retry, 176). **Declined** → batch E: `UPDATE [CCTxs] SET [Number]=NULL,[OffLineStatus]=Declined=2; UPDATE [Payments] SET [Status]=Deleted=14 (Type stays 11); UPDATE [Orders] SET [CloseDate]=NULL,[Paid]=[Paid]-ABS(Amount),[Status]=IIF(old Paid=@Amount, Open=0, Pending=6),[Tip]-=CCTxs.Tip,[Total]-=same; DELETE [OrdersTips] WHERE [OrderID] AND [PayID] AND [Type]=CC=1` (192-214; `BatchID` untouched; for negative offline Returns `Paid` moves the wrong way — open question). With zero failures: `UPDATE [Terminals] SET [OffDate]=NULL,[OffSent]=NULL` (236-240). In **this build** `CloseBatch` never reaches frmOfflineProcess (5.10).

### 9.10 Bank (drawer) sessions, batch close, terminal registration, transaction reset

Bank Swap (13243-13381): swap-out batch `UPDATE [Payments] SET [BankID]=NULL WHERE [Status] NOT IN (1,8,9) AND [BankID]=@BankID; UPDATE [Bank] SET [ShiftEnd]=GETDATE(),[Status]=SwapOut=2` (13337-13350), then `LocalSetting.BankID=null`; swap-in batch `UPDATE [Payments] SET [BankID]=@BankID WHERE [Status] NOT IN (1,8,9) AND [BankID] IS NULL` — **adopts open payments of all terminals** — `; UPDATE [Bank] SET [TerminalID],[ShiftBegin]=GETDATE(),[Status]=SwapIn=1` (13358-13373), then `LocalSetting.BankID=mBankID` and `Exatouch.cfg` rewritten (13374-13377). `frmBank` INSERTs `Bank (Status=Open=0)` (frmBank.cs:272-275); `frmBankClose` INSERTs one `BankDetails` row per tender (`Amount` counted, `Expected`) and `UPDATE [Bank] SET [Delta],[Status]=Closed=3,[PayOutIn]=SUM(BankMod.Cash),[RefCash]` (frmBankClose.cs:143-164) without clearing `LocalSetting.BankID`. Batch Close in this build: 5.10. Terminal registration at start: frmMain.cs:1927-1943. `Lib.ResetTrxs` (Lib.cs:6345-6444; DEMO at 100 orders, `frmReserved.cs:946`) TRUNCATEs every order/payment/bank/clock/log table and resets `FloorPlan`, `Staff`, `Terminals`, `Items` — identities restart at 1.

### 9.11 Peripheral writers of order-side rows (listed, not traced row by row)

These touch order, delivery, bank or item rows from screens outside the flows above. A test that drives them must expect the writes; the column lists are from a grep of the SQL text, not a full trace.

| Where | Write | When |
|---|---|---|
| frmDelManager.cs:901, 917 (`btnResetStatusClick`) | `UPDATE [OrdersDelivery] SET StaffID=NULL, Delivered=NULL, Out=NULL, Ready=NULL, Status=@Status OUTPUT DELETED.StaffID`; `UPDATE [Staff] SET DeliveryStatus=Available` for the released driver | delivery-manager Reset Status |
| frmDelManager.cs:986, 1030 (`btnStatusClick`, "Driver(s) removed.") | `UPDATE [OrdersDelivery] SET StaffID=NULL` (only while `Status<=Ready`); `UPDATE [Staff] SET DeliveryStatus=@NewStatus` recomputed from the driver's remaining rows | driver removed from an order |
| Transactions\Delivery\frmMap.cs:100, 143 (`_Remove`) | same pair of statements from the delivery map | driver removed on the map |
| Transactions\Delivery\frmReorder.cs:115 | `UPDATE [Customers] SET DeliveryInstruction` | re-order dialog saves instructions |
| Transactions\Delivery\frmDeliveryAddress.cs:150/179/188 | `CustomersAddresses` DELETE / INSERT / UPDATE | delivery address picker |
| frmRegister.cs:7998 | `UPDATE [Customers] SET [Distance]` | in-house delivery customer lookup with address verification |
| Transactions\SubForms\frmSendEmail.cs:138/147 | `INSERT INTO [Customers] (FirstName, LastName, Email, FirstStaffID)` or `UPDATE [Customers] SET FirstName, LastName, Email` | e-mail receipt prompt for an unknown / known customer |
| Transactions\SubForms\frmInfo.cs:139/150/157 | `UPDATE [Items] SET Unavailable=GETDATE()` (or NULL to un-86); `UPDATE [FreshSheet] SET Qty=0` | item-info popup "86 this item" |
| Transactions\Bank\frmBank.cs:169/280/290/315 | `INSERT INTO [Bank] … SELECT` one row per day (duplicate); `UPDATE [Bank]` (edit); `INSERT INTO [BankMod] (…, Cash=_Total-_Open, Notes=adjustment note)` when editing a SwapIn bank; `UPDATE [Bank] SET Active=0` ("Void bank?") — the last one changes the "bank swapped in" precondition behind `Payments.BankID` | Bank Manager |
| Transactions\SubForms\frmTakePayment.cs:156 → AccountPaymentManager.cs:201-256 | `INSERT INTO AccountPayments (CustomerId, TenderType, PayType, CCTxId, AmountApplied, AmountPaid, AmountReceived, Surcharge, Note, Date, CreatedAt, TerminalId, TerminalName, StaffName, CreatedBy, TransactionId, Reference, Rate=0, Deleted=0); SELECT SCOPE_IDENTITY()` plus one `AccountPaymentDetails` row per allocated invoice (`[Order]`, OrderId, Name, Amount) — committed only after the CRM upload succeeds, rolled back otherwise; the card's `CCTxs` row (written by `SaveTrx` with `PayID=0`) survives either way | A/R "Take Payment" |
| frmRegister.cs:14842, 14934, 14948, 14951, 14964 | `OrdersFPSplit` UPDATE/INSERT `(FPID, OrderID, Status)` when a Split table is the source or target of Move/Join (Status = the target table's pre-Split status) | floor-plan Move / Join with a split table |
| Lib.cs:5031-5062 | `OrdersFPSplit` deletes per split/non-split combination after a join (source row always; target row when one remains on the same table; source row when FPIDs differ) | `Lib.JoinOrder` floor-plan branch |
| csDualPriceManager.cs:690-995 | full SET lists of the completion rewrite: Orders `BaseAmount, ItemTotal, Tax, [DeliveryFee], Total`; OrdersDetails `DiscUnit, Price, PriceBase, PriceDisplay, PriceOriginal, PriceSell, PriceSinTax, PriceTax, PromoPrice, PromoUnit, PromoUnitDisc`; OrdersTax `Qty, QtyExempt`; OpenPromoDetails `Value`; OrdersDelivery `DeliveryFee`; coupon Payments `Amount`; club Payments `Amount, TFUsed` | dual-price completion (5.7) |
| Reports\csPrint.cs:500 | `UPDATE [Payments] SET [TerminalID]=Orders.TerminalID WHERE PayID AND TerminalID IS NULL` | receipt print of a payment row without a terminal (normally a no-op) |
| Settings\frmFieldManager.cs:467/479/491/503/511 | `UPDATE [OrdersDetails]` ×4 and `UPDATE [Orders] SET [CustomID]=NULL` when a custom field/order type is deleted | Settings → Field Manager |
| Staff\frmPayout.cs:140 | `UPDATE [OrdersDetails] SET [PayOutID] WHERE [TxDetailID]` — neither column exists in the live schema: dead/legacy code that would throw if reached | Staff payout |
| Items\frmPromo.cs:548 | `DELETE FROM [OrdersPromos] WHERE PromotionID AND OrderID IS NULL` | a promotion's Serial flag switched off |

## 10. State machines

### 10.1 `Orders.Status`

| from | to | trigger | where |
|---|---|---|---|
| (none) | Open=0 | first `SaveOrder` of a cart (Save, Pay, Tab Auth, Split source, Fire Now, auto-save) | 2030 (`@Status` 1858) |
| (none) | Open=0 | split child (Status copied from the original) | 19615 |
| (none) | Open=0 | re-created order after a frmRefund Void | frmRefund.cs:2961-2972 |
| (none) | Completed=1 | Pay In / Pay Out | 16135 |
| (none) | Refunded=4 | refund order (`RefID`=original) | frmRefund.cs:2448 |
| (none) | OpenRefund=10 | open-ended refund | frmRefund.cs:1808 |
| 0 or 6 | Pending=6 | each non-discount tender committed (also the last one) | 17959-17979 |
| 6 (or 0) | Completed=1 (+`CloseDate`, `BatchID`, `Tip`, `Surcharge`) | balance 0 and not partial — background thread; also when payments are still Auth=6 | 18637-18651 |
| 0 or 6 | Completed=1 (+`CloseDate`, `Paid=Total+Tip`, `StaffID`) | delivery manager cash/3PDS close | frmDelManager.cs:606 |
| 0 or 6 (unchecked) | 1 if `Paid+amount >= old Total` else 6 (+`PaidAtTable=1`) | pay-at-table `spApplyPayment` | db-procs.txt:70-72 |
| 0 or 6 | Pending=6 | `WithError` after another tender completed | 19058 |
| 0 | Voided=5 (+`CloseDate`, `ReasonID`, `Note`) | recall-list Void | frmGetOrders.cs:429 |
| 1 | Voided=5 (+`RefundAmt=Paid`, `VoidDate`) | `OrderManager.VoidOrder` (eGift activation cancelled at completion) | OrderManager.cs:167-174 |
| 1 (non-pending, `RefundAmt=0`) | Voided=5 (+`RefundAmt=_Ref`, `VoidDate`) | frmRefund Void | frmRefund.cs:2706-2725 |
| 1 | 1 (rewritten, `RefundAmt += _Ref`) | frmRefund Refund (full or partial) | frmRefund.cs:2706-2725 |
| 6 | 0 when `_Ref==_Remain`, else 6 (`Paid -= _Ref`) | frmRefund Refund of a Pending order | frmRefund.cs:2706-2725 |
| 1 or 6 | 0 when no live payment remains, else 6 (`Paid -= Amount`) | `VoidAuth` of a non-tab payment with `pVoidOrder=false` (frmComplete / frmReView Void) | csPayment.cs:1137-1139 |
| 1 or 6 | 0 if `Paid` equalled the amount else 6 (`CloseDate=NULL`) | offline settlement declined | frmOfflineProcess.cs:200 |
| 0 or 6 | JoinIn=2 (+`RefID`=target, `Total=0`, `Tax=0`, `CloseDate`, `StaffID=Global.UserID`) | join source | Lib.cs:5013 |
| T | Pending=6 when source and target statuses differ | join target | Lib.cs:4996-5000 |
| any | Undeliverable=11 | delivery manager Undeliverable | frmDelManager.cs:706 |
| any | unchanged | re-save (`Status` not in the UPDATE SET list); Reassign/Move/Print/tab auth | 2093; 19246; 14867; 9933; 17490 |
| — | Deleted=3, RefPending=7, VoidPending=8, RefProcessing=9 | **no writer found** in the traced code (RefPending=7 is only read by frmSearch) | — |

### 10.2 `Payments.Status`

| from | to | trigger | where |
|---|---|---|---|
| (none) | Open=0 (`RefundAmt=0`) | `SaveOrder` inserts every staged tender before processing | 2595 |
| (none) | Completed=1 | Pay In/Out; synthetic cash-back row; delivery cash close; `spApplyPayment` | 16152; 18352; frmDelManager.cs:616; db-procs.txt:63 |
| (none) | Refunded=8 (`Amount<0`) | refund-order payment | frmRefund.cs:2774 / 2688 |
| (none) | OpenRefunded=13 | open-ended refund | frmRefund.cs:1840 |
| (none) | Open=0 | re-created order (ClubDiscount rows only) | frmRefund.cs:2983 |
| 0 / RemoteError=5 / 11 | Completed=1; Auth=6 when `mIsAuth` (restaurant Credit, restaurant eGift with balance); Refunded=8 for exchanges | per-tender commit (batch A) | 17938-17967 |
| 0 | TabAuth=11 (`Value=AuthAmount`) | tab auth initiated | 17486-17514 |
| 11 | Completed=1 (`Amount+=tip`, `CloseUserID=Orders.StaffID`) | `Complete(pID, null)` non-multitouch; then batch A rewrites Status=1 | csPayment.cs:495-500; 17967 |
| 6 | Completed=1 | Complete Auth window; Batch Close (this build: every un-batched row); non-DEBUG batch close (eGift rows); delivery credit batch H; offline approved (`Type` 11→3) | csPayment.cs:495, 167-174, 1252; frmDelManager.cs:669; frmOfflineProcess.cs:142 |
| 0 | Deleted=14 (`OrderID=-OrderID`) | `WithError` for Credit/Debit/EBT/eGift/Offline/OnAccount/Certificate rows | 18996 |
| 0 | Deleted=14 | recall-list Void | frmGetOrders.cs:425 |
| any (offline) | Deleted=14 (`Type` stays 11) | offline settlement declined | frmOfflineProcess.cs:196 |
| 0 / 1 / 6 / 11 | Voided=9 (`CloseDate`; tab rows `Amount=Value`) | `VoidAuth` (frmComplete/frmReView Void, floor-plan Join of a tab, partial-approval cancel, tab closed by another tender, frmRefund card rows) | csPayment.cs:1116-1118 |
| 1 / 6 | Voided=9 (`RefundAmt=Amount`, `CloseDate`) | `OrderManager.VoidOrder` | OrderManager.cs:152-156 |
| 1 / 6 / 9 | Voided=9 (`RefundAmt += Amount`) | frmRefund Void, or an Auth payment inside a Refund | frmRefund.cs:2817 |
| 1 | 1 (`RefundAmt += RefAmt`) | frmRefund Refund | frmRefund.cs:2768 |
| any | Voided=9 | join source's ClubDiscount rows | Lib.cs:4944 |
| 0 (`Type`=Check=1) | Completed=1, `Type`→CheckCashBack=14 when the check exceeds the due | cash-back section | 18322-18373 |
| 0 with PayID | unchanged Status, `OrderID=-OrderID` | tender removed (`_ResetPay`) then saved | 2564 |
| — | RemoteQueue=2, RemoteProgress=3, RemoteError=5 | read at 16445 (RemoteError rows are re-processed); **no writer** in the traced code (remote/ScanToPay device) | — |

### 10.3 `CCTxs.Status` / `TxType` / `OffLineStatus`

| from | to | trigger | where |
|---|---|---|---|
| (none) | `Status` Authorized=0 when `TxType`=Auth=0 else Completed=1; `TxType` 0/2/3/5 | `SaveTrx` after an approved Sale/Auth/Force/Return (register card, eGift, refund Return, open-ended Return, A/R card, offline fake approval) | csPayment.cs:82-104 |
| (none) | `Status` 1 (or 6, not a member), `TxType` 4, `BatchClose=GETDATE()` | `spApplyPayment` | db-procs.txt:58-59 |
| 0 or 1 | TabAuth=2 (`TxType` stays 0) | tab auth initiated | 17486 |
| 0 or 2 | Completed=1, `TxType`=Capture=1, `CloseDate`, `PNRef`/`TransmitID` overwritten, `Tip` | `csPayment.Complete` (Tab Complete, Complete window, delivery credit, non-DEBUG batch close) | csPayment.cs:482-491 |
| 0 or 1 | Completed=1, `TxType`=1, `Tip=0`, `CloseDate` | partial approval accepted | 17432 |
| any | Voided=3, `TxType`=Void=6, `Tip=0` (+`Number=NULL`, `OffLineStatus`=Voided=4 for offline) | `VoidAuth` | csPayment.cs:1108-1114 |
| any | Voided=3, `RAmt=Amount`, `Tip=0` (+offline reset) | `OrderManager.VoidOrder`; frmRefund void rows (`RAmt=RefAmt`) | OrderManager.cs:98-108; frmRefund.cs:2823-2832 |
| 1 / 0 / 4 | `TxType`=Adjustment=4, `TransmitID`, `Tip`=new or NULL | frmReView tip adjust | frmReView.cs:151-171 |
| any with `BatchClose NULL AND Number NULL` | `BatchClose=@Now`; 0→1 with `CloseDate`; `TxType` 0→1 (TabAuth rows included) | Batch Close in this build | csPayment.cs:176-179 |
| `OffLineStatus` NULL | Approved=3 (`Number=NULL`, `Status=1`, `TxType` 1/2) / Failed=1 / Declined=2 (`Number=NULL`) | offline settlement | frmOfflineProcess.cs:138 / 176 / 192 |
| `Number` NULL | RSA-encrypted card data | offline sale/refund; eGift redeem | 17237; frmRefund.cs:2310, 1707; CardTransactionManager.cs:28 |

### 10.4 `FloorPlan.Status` (and `OrdersFPSplit`)

| from | to | trigger | where |
|---|---|---|---|
| Open=1 / Busy=2 | Ordered=3, `OrderID=MainID`, `InUse=0`, `LastAct` | first save of an order on the table (if the csObject guard passes) | 2076-2084 → csObject.cs:472 |
| Check=4 | Ordered=3 | popup Ordered/FullDebit on a non-split, non-tab table | 15132 → 15208 |
| any except Split=10 | TabAuth=7, `LastAct` | tab auth initiated | 17521 / csObject.cs:472 via 17532 |
| any (not Split) | Check=4, `OrderID` | guest check printed on a tab-less table | 9959 / 9971 |
| any | Split=10, `OrderID=MainID`, `InUse=0` | split by item; split-Join target | 19820-19821; 14972 |
| any (with control on screen) | CleanUp=6, `OrderID=NULL`, `InUse=0`, `LastAct` | completion with no split rows left; grid Void; Move/Join source; type change; orphan/LRS frame; `_OnComplete` cleanup | 18890/18923; frmGetOrders.cs:474; 14886/15014; 20055; 2954; 9377 |
| any (not Split) | CleanUp=6 (`OrderID` kept) | `OrderManager.VoidOrder` | OrderManager.cs:180-183 |
| any | Authorized=5 | completion where the last tender left an Auth payment (restaurant); `OrdersFPSplit.Status=5` on a Split table | 18902; 18868 |
| Split=10 | remaining sub-order's status, `OrderID`=that order | completion / `_OnComplete` with exactly one `OrdersFPSplit` row left | 18895-18902; 9401-9424 |
| Open=1 / NULL | source's Status / source's OrderID | Move target | 14880 |
| CleanUp=6 | Open=1, `OrderID=NULL`, `LastAct`=client clock | Reset All | 19309-19325 |
| any | Open=1 or Busy=2, `OrderID=NULL` | popup Available/Occupied (Orders row untouched) | 15190 |
| `InUse` 0 | 1 | recall onto the table; table click; "use this table?" | 8674; 15041; 15220 |
| `InUse` 1 | 0 (+`LastAct=GETDATE()`) | every release: `Clear()`, form close, popup command, first check print, Reset All | 1001/1012; 2979; 15208; 9971; 19330 |
| `OrdersFPSplit.Status` any | Ordered=3 | `VoidAuth` of a non-tab payment | csPayment.cs:1124 |
| `OrdersFPSplit` rows | deleted | Available/Occupied; grid Void; non-split Join of a split member; completion; editor delete | 15162/15179; frmGetOrders.cs:462; Lib.cs:5031-5062; 18877/18895; frmSetFloorMap.cs:617 |

## 11. Atomicity and failure modes

1. **Nothing in the legacy path is rolled back.** The only transactions are `OrderManager.VoidOrder` and `TakeAccountPayment`, plus the two stored procedures. Every multi-statement CommandText commits statement by statement; because `XACT_ABORT` is never set, a statement-level runtime error inside a batch leaves earlier statements committed **and** runs the later ones, while a compile-time error (unsupplied parameter, missing object) fails the whole batch before anything is written. The realistic failure in an FK-less database is a missing parameter (`@OrderID`/`@RefID`/`@CCTxID` null after a swallowed earlier failure).
2. **Residue of a failed `SaveOrder` on a new order**: an `Orders` row (`Status=0`, `InUseID=TerminalID`, never released), its `OrdersLog` Create row, possibly the `FloorPlan` update, a prefix of `OrdersDetails` lines with stock already decremented — no `OrdersTax`, `Payments` or `OrdersTips`. Lines that failed to insert keep `ODID` NULL in memory and are inserted again by a later successful save. A wrapper failure at the `Orders` INSERT surfaces as an NRE at 2049 after the error modal; the existing-order UPDATE batch can lose `OpenPromoDetails` (DELETE committed, UPDATE failed, NRE at 2133).
3. **Money moves before rows in every processor flow.** Card approval (`Process`) precedes `SaveTrx`; capture precedes the `CCTxs` UPDATE in `Complete`; the device Void precedes the `VoidAuth` batch; refund Returns, gift `Activate/Redeem/AddValue` and Factor4 calls precede every frmRefund row (write 3 onwards). A failure after the processor step leaves funds moved with, at most, a `CCTxs` row recorded. Within `VoidAuth`, a failure at the `Orders` statement leaves `CCTxs`/`Payments` voided and the floor plan reset but `Orders.Paid` and `OrdersTips` untouched; the `Terminals` reset still runs because the wrapper returned null.
4. **Per-tender commit is swallowed.** Batch A runs through `Lib.ExecNonQuery` (18036): on a SqlException memory says Paid, the DB still says `Status=0`, and completion proceeds — the close batch then marks the order `Completed=1` with `Paid` short. Loyalty points consumed at 17907 are never restored, by `WithError` or otherwise.
5. **Completion is asynchronous and swallowed.** If batch C fails, `Orders.Status` stays 6 with `CloseDate` NULL and no Close `OrdersLog` row, yet the loyalty/customer/gift writes (18700-18830) still run; on floor-plan installs the thread then dereferences null at 18838. Any unhandled exception on that thread (`ArgumentOutOfRangeException` for a $0 certificate, `ExecScalar()` null at 18690, division by zero at 18681 on a $0 total) goes to `AppDomain.UnhandledException` → popup → **process termination** (Start.cs:70-72). Poll for `Status=1 AND CloseDate IS NOT NULL`; `InUseID=NULL` from the UI thread can land first.
6. **`WithError` compensation is partial**: card-like rows are soft-deleted (`OrderID=-OrderID, Status=14`), Cash rows are not; a CFD tip stays in `Orders.Total` except for eGift; `Orders.Status=6` is set when something was paid. Certificate failures abort before `WithError` (RowNotInTableException), leaving the certificate row `Status=0`.
7. **Partial-approval and tab paths**: Cancel leaves `Payments.Status=9` with `Amount=AuthAmount`, `CCTxs` voided, `Orders.Paid`/`OrdersTips` untouched and `Terminals.OffDate` cleared. A failed Tab Complete (`-1`) returns with the tab row still 11 and `TabAuthID` intact, possibly after zeroing TD/TF (16956) and inserting a `CcTxFailures` row. In batch 17486, a failing `Orders` UPDATE leaves `CCTxs.Status=2` with `TabAuthID` NULL and `Payments.Status=0`.
8. **Offline paths**: `Offline(true)` persists offline mode to `Exatouch.cfg` **before** the card is read (17211; frmRefund.cs:1691) — it survives restarts; a failed offline `SaveTrx` then makes the raw batch at 17255 (or frmRefund.cs:1719) throw "parameter @CCTxID not supplied". `Terminals.OffDate/OffSent` are cleared by **every online VoidAuth** (csPayment.cs:1173-1181) and by every non-offline voided row in frmRefund (2846), hiding the offline banner while unrelated offline rows may still be pending (open question).
9. **`OrderManager.VoidOrder`** rolls its seven writes back on any exception (declined void with a non-blank message, eGift payment, NULL `CCTxID`, missing `CCTxs`/`Items` row, Factor4 without customer, NULL `FloorPlan.Status`) but not the processor voids, gift credits, Factor4 refund or printed slips already done; a retry depends on the processor answering "already voided". An offline terminal or uninitialised processor yields a **committed local-only void**. Its X-locks block legacy readers for the duration of the processor/HTTP calls; readers never observe `Orders.Status=5` with payments still 1/6.
10. **frmRefund**: write 3 failing → `mOrderID` null → write 4 throws "parameter not supplied"; a failing raw statement between 2436 and 2854 aborts unhandled with earlier statements committed; Cancel inside the payment try/catch abandons payments already voided/refunded at the processor earlier in the loop; a Re-create whose header INSERT fails leaves the voided original locked. Open-ended: `Process` succeeded but the `Orders` INSERT failed → a Return `CCTxs` row with no order, then `Print.Refund(null)` and "Refund completed." anyway.
11. **Join / Split / Bank swap** are chains of autocommits: a failing split child INSERT continues the loop with `@OrderID` NULL; a throw between the child INSERT and the line moves leaves an empty child; the original's `OrdersTax` is already deleted (19622). Join failures leave sources half-moved and still return `DialogResult.Yes`, so the register rewrites the target's `Tax/Total` (14060). Bank swap-out can commit `Payments.BankID=NULL` while the bank still says SwapIn.
12. **Locks and stale state**: `InUseID` is set as the last statement of `_LoadOrder` and cleared by `Clear()` regardless of Status; a killed terminal leaves it set; frmRefund can clear another terminal's lock after a refused load; deleting the last line of a recalled order or closing frmRefund by any path other than Cancel/Clear leaves the lock. `Global.MgrID` persists for the session after any override and is stamped into every later `OrdersLog` Manager row and `BankMod.MngrID`.
13. **Clocks**: `OrdersTax.CDate` (2640, 19710, frmRefund.cs:2514), `Log.CDate`, `GiftCards.DateSold`, `VoidDate` (OrderManager.cs:169), `Payments.CloseDate` in `VoidOrder` (154) and Reset All's `LastAct` use the **client** `DateTime.Now`; everything else uses `GETDATE()`. Do not assert equality between the two.
14. **Concurrency on `Global.SqlConnection`**: the completion thread and the UI thread share it (MARS); the `_SqlConnException` reconnect loop re-executes a CommandText forever if the cause is not the connection.

## 12. QA assertion recipes

Placeholders: `@O` order id (read the on-screen order number, or `SELECT MAX(OrderID) FROM Orders` right after the flow), `@Term` = `SELECT TerminalID FROM Terminals WHERE MAC=<this MAC>` (or `Name=<NickName>`), `@User` = the logged-in `Staff.StaffID` (1 in this DEBUG build), `@Bank` = `Global.LocalSetting.BankID` from `Exatouch.cfg` (NULL when no drawer session is swapped in), `@T0` = a `GETDATE()` snapshot taken before the flow. Poll for completion (`Status=1 AND CloseDate IS NOT NULL`, up to ~5 s) before asserting anything written by the background thread. Where the DEBUG build changes a value it is noted.

The blocks below are not runnable as pasted: declare the placeholders first, or pass them with `sqlcmd -v` substitution.

```sql
DECLARE @O int = 245;                                                     -- the order under test
DECLARE @Term int = (SELECT TerminalID FROM Terminals WHERE MAC = '<this MAC>');
DECLARE @User int = 1;                                                    -- Staff.StaffID of the logged-in user (1 in this build)
DECLARE @Bank int = NULL;                                                 -- LocalSetting.BankID from Exatouch.cfg, NULL when no drawer session
DECLARE @I int = 100;                                                     -- an Items row with StockType=0 used in the flow
DECLARE @T0 datetime = '2026-09-07 21:00:00';                             -- captured with GETDATE() before driving the flow
```

**Pin the lab configuration before trusting an expected value.** Each of these flips at least one column below: `Settings.Account.Merchant.IndustryType` (restaurant: card sales are Auth → `Payments.Status=6`, `CCTxs.TxType=0`, Tab Auth/Complete visible; retail: Sale → 1/2); `Printer.RemoteOption.Firing` and whether a remote printer/KDS exists (`OrdersDetails.Fire`/`Printed` NULL vs 1 vs 0/=Qty); `Global.TouchScreen` (= a VGA CFD attached: `OrdersTips.Type` 1 vs 7, the CFD tip step before the card read, the double tip add in `Complete`); `Account.TenderFee.Surcharge` (fake 3 % surcharge on every credit sale in this build); the drawer state in `Exatouch.cfg` (`Payments.BankID`, `BankMod` rows); the configured tax slots (`OrdersTax` rows beyond Types 0-3); `CustomFields` reason ids 39/40/43; and the fake processor's 1-in-33 decline on **every** transaction type — a harness must detect the decline dialog and retry or clean up on sales, voids, refunds and captures alike.

### 12.1 New cash sale (retail)

UI: Register → category → item(s) → Payments tab → Cash → amount/collected in `frmCash` → OK (or Exact Cash).

```sql
SELECT Status, Paid, Total, Tax, Tip, CloseDate, BatchID, InUseID, TerminalID, StaffID, OrderType, Printed, Source, TabAlert, RefundAmt, Surcharge
FROM Orders WHERE OrderID=@O;
-- Status=1 (poll), Paid=Total, CloseDate>=@T0, BatchID=0 (NULL only with a tax-code item), InUseID NULL, TerminalID=@Term,
-- StaffID=@User, OrderType=0, Printed=0, Source=0, TabAlert=0, Tip=0.00, RefundAmt NULL, Surcharge NULL
SELECT Type, Status, Amount, Value, Tip, CloseDate, CloseUserID, CCTxID, BankID, TerminalID, UserID, RefundAmt, TDUsed, TFUsed, Reference, DeliveryFee
FROM Payments WHERE OrderID=@O;
-- 1 row: Type=0, Status=1, Amount=Total, Value=collected, Tip NULL, CloseDate>=@T0, CloseUserID NULL, CCTxID NULL, BankID=@Bank or NULL,
-- TerminalID=@Term, UserID=@User, RefundAmt=0, TDUsed=0, TFUsed=0, Reference NULL, DeliveryFee=0
-- Exact Cash may add a second Type=0 row with Amount=0 (open question 6) — filter Amount<>0 when counting
SELECT Type, ID FROM OrdersLog WHERE OrderID=@O ORDER BY Type;            -- (0,@User) then (1,@User)
SELECT ODID, LinePos, ItemID, Qty, Deleted, UserID, Printed, Fire, QtyDel, RefQty, PriceSell
FROM OrdersDetails WHERE OrderID=@O ORDER BY LinePos;                     -- LinePos 0..n-1, Deleted=0, UserID=@User, QtyDel/RefQty NULL;
--   Printed NULL and Fire NULL with Firing off and no remote printer; Fire=1 when Firing is on and nothing printed; Fire=0 and Printed=Qty only after a remote/KDS print
SELECT Type, Qty, QtyExempt FROM OrdersTax WHERE OrderID=@O ORDER BY Type; -- Types 0,1,2,3 present; Type 0 Qty = ROUND(SUM(PriceSell*Qty) of TaxableA rows,2)
SELECT QtyOnHand, QtySold FROM Items WHERE ItemID=@I;                      -- before-qty / before+qty (StockType=0 items only), already true after the Save step
SELECT COUNT(*) FROM Payments WHERE OrderID=-@O;                            -- 0
```

### 12.2 New card sale

UI: items → Credit (or Debit) → amount prompt OK → device (fake in this build; ~3 % random declines → retry).

```sql
SELECT P.Type, P.Status, P.Amount, P.CCTxID, P.Reference, P.CloseDate, P.Tip,
       C.TxType, C.Status AS CCStatus, C.AType, C.AuthCode, C.CCNumber, C.CardHolder, C.ExpDate, C.Partial, C.TerminalID, C.BatchClose, C.CloseDate AS CCClose, C.TxRespons, C.Number
FROM Payments P JOIN CCTxs C ON C.CCTxID=P.CCTxID WHERE P.OrderID=@O;
-- Type=3 (also for Card on File), Status=1 (6 in restaurant mode), CCTxID NOT NULL, Reference NULL (batch A overwrote SaveTrx's AuthCode), CloseDate>=@T0, Tip NULL,
-- C.TxType=2 (0 in restaurant mode), CCStatus=1 (0 restaurant), AType=2 (0 Debit), LEN(CCNumber)=8 starting '****', Partial=0,
-- C.TerminalID=@Term, BatchClose NULL, CCClose NULL, Number NULL; fake processor: LEN(C.AuthCode)=6, CardHolder='Fake credit card', ExpDate='0130', TxRespons NULL;
-- with Account.TenderFee.Surcharge on: C.Surcharge=P.Surcharge=ROUND(amount*0.03,2) and Orders.Total/Surcharge include it
SELECT Status, Paid, CloseDate FROM Orders WHERE OrderID=@O;              -- 1, amount, NOT NULL (also Completed while P.Status=6 in restaurant mode)
SELECT AuthDate, Sent FROM Terminals WHERE TerminalID=@Term;              -- AuthDate NOT NULL after the first online Credit approval, Sent NULL
SELECT COUNT(*) FROM CCTxFailures WHERE OrderID=@O;                       -- 0 (>=1 per decline)
```

### 12.3 Held order recalled and paid

UI: items → **Save** → Get Order → select → OK → Cash → OK.

```sql
-- after Save
SELECT Status, InUseID, Paid, Printed, Total FROM Orders WHERE OrderID=@O;         -- 0, NULL, NULL, 0, total
SELECT COUNT(*) FROM Payments WHERE OrderID=@O;                                    -- 0
SELECT Type, ID FROM OrdersLog WHERE OrderID=@O;                                   -- exactly (0,@User)
SELECT QtyOnHand FROM Items WHERE ItemID=@I;                                       -- already decremented (stock moves at Save, not at pay)
-- after Get Order, before paying
SELECT InUseID FROM Orders WHERE OrderID=@O;                                       -- @Term
-- after Cash (the tender click re-runs SaveOrder because the grid flips Modified — section 3.1)
SELECT Status, InUseID, Paid, Printed FROM Orders WHERE OrderID=@O;                -- 1 (poll), NULL, Total, 0
SELECT COUNT(*) FROM OrdersTax WHERE OrderID=@O AND Type IS NOT NULL;              -- same count as after Save (OTIDs reused FIFO)
SELECT Type, ID FROM OrdersLog WHERE OrderID=@O ORDER BY Type;                     -- (0,@User),(1,@User) — no second Create row
```

### 12.4 Split by item

UI: order with ≥2 lines (saved or not) → Split → confirm tips/promo dialogs → in `frmSplitItem` move lines to group 1 → OK. `@Orig` = original, `@Child` = `SELECT MAX(OrderID) FROM OrdersLog WHERE ID=@User AND Type=0`.

```sql
SELECT OrderID, Status, Paid, Total, Tax, Tip, Exchange, CustExempt, StaffID, CustomerID, OrderType, InUseID
FROM Orders WHERE OrderID IN (@Orig, @Child);
-- child: Status=original Status (0), Paid NULL, Exchange NULL, CustExempt NULL, StaffID/CustomerID/OrderType copied, InUseID NULL; each Total = its own lines
SELECT OrderID, COUNT(*) AS Lines, SUM(CASE WHEN Shared=1 THEN 1 ELSE 0 END) AS ShareRows
FROM OrdersDetails WHERE OrderID IN (@Orig, @Child) AND Deleted=0 GROUP BY OrderID;  -- moved lines carry OrderID=@Child and their ORIGINAL CDate
SELECT OrderID, Type, Qty, QtyExempt FROM OrdersTax WHERE OrderID IN (@Orig, @Child) ORDER BY OrderID, Type;  -- one row per taxed slot per sub-order
SELECT Type, ID FROM OrdersLog WHERE OrderID=@Child;                                  -- (0,@User)
SELECT COUNT(*) FROM OrdersTips WHERE OrderID=@Orig AND Type<>2;                       -- 0 (non-group tips deleted)
-- floor plan only:
SELECT OrderID, Status FROM OrdersFPSplit WHERE FPID=@FPID;                            -- one row per sub-order, Status=3 (7 for @Orig with a tab auth)
SELECT Status, InUse, OrderID FROM FloorPlan WHERE FPID=@FPID;                         -- 10, 0, @Orig
```

### 12.5 Tab auth, then Tab Complete with a tip (restaurant mode)

UI: table/order → Tab Auth → card → (frmTabLimit) → label → returns to floor. Later: recall → Tab Complete → tip prompt (non-multitouch: `frmMessageValue`; multitouch: CFD) → done.

```sql
-- after Tab Auth
SELECT O.Status, O.Paid, O.TabAuthID, O.TerminalID, O.TabMin, O.TabLimit, O.Label, O.InUseID,
       P.Status AS PayStatus, P.Value, P.CCTxID, P.Reference, P.CloseDate,
       C.Status AS CCStatus, C.TxType, C.AType, C.Partial, C.CloseDate AS CCClose
FROM Orders O JOIN Payments P ON P.PayID=O.TabAuthID JOIN CCTxs C ON C.CCTxID=P.CCTxID WHERE O.OrderID=@O;
-- Status=0, Paid=0 for a first-save cart (NULL for a recalled held order: the re-save keeps NULL, 2113), TabAuthID=P.PayID, TerminalID=@Term, TabMin=<entered>, TabLimit NULL (AuthAmount on partial), Label set, InUseID NULL;
-- PayStatus=11, Value=TabMin, CCTxID NOT NULL, Reference=AuthCode, CloseDate NULL; CCStatus=2, TxType=0, AType=2, Partial=0, CCClose NULL
SELECT Status FROM FloorPlan WHERE FPID=(SELECT FPID FROM Orders WHERE OrderID=@O);   -- 7 (10 if the table was split)
-- after Tab Complete with tip @Tip (assert after Status=1)
SELECT O.TabAuthID, O.Status, O.Paid, O.Tip, O.Total, P.Status AS PayStatus, P.Amount, P.Tip AS PayTip, P.CloseDate, P.CloseUserID,
       C.Status AS CCStatus, C.TxType, C.Tip AS CCTip, C.CloseDate AS CCClose,
       (SELECT COUNT(*) FROM OrdersTips WHERE PayID=P.PayID) AS TipRows
FROM Orders O JOIN Payments P ON P.OrderID=O.OrderID AND P.Status<>14 JOIN CCTxs C ON C.CCTxID=P.CCTxID WHERE O.OrderID=@O;
-- TabAuthID NULL, Status=1, O.Tip=@Tip, O.Total=pre-tip total+@Tip, PayStatus=1, CloseDate NOT NULL, CCStatus=1, TxType=1, CCTip=@Tip (NULL when 0), CCClose NOT NULL, TipRows=1;
-- non-multitouch (Complete(pID,null)): P.Amount = authorised balance + @Tip (+@Tip again when Global.TouchScreen — open question), P.CloseUserID=O.StaffID, O.Paid includes the tip;
-- multitouch: P.Amount from the register row, CloseUserID NULL; OrdersTips.Type = 1 (CC) or 7 (CFD) depending on Global.TouchScreen
```

### 12.6 Void from the register (Refund screen → Void) of a completed card order

UI: Register → Refund → Find Order → select `@O` → all lines checked → Void → confirm → "Transaction voided" style success → (Re-create unchecked).

```sql
SELECT o.Status, o.VoidDate, o.RefundAmt, o.Paid, o.Tip, o.CloseDate, o.InUseID,
       (SELECT COUNT(*) FROM Orders WHERE RefID=@O) AS RefundOrders,
       p.Status AS PayStatus, p.RefundAmt AS PayRefund, p.CloseDate AS PayClose,
       c.Status AS CCStatus, c.TxType, c.RAmt, c.Tip AS CCTip,
       (SELECT COUNT(*) FROM OrdersTips WHERE OrderID=@O) AS TipRows
FROM Orders o JOIN Payments p ON p.OrderID=@O AND p.Amount>0 AND p.Type NOT IN (2,5,15) AND p.Status<>14   -- Coupon/Discount/ClubDiscount rows stay Status=1 and are never voided
LEFT JOIN CCTxs c ON c.CCTxID=p.CCTxID WHERE o.OrderID=@O;
-- Status=5, VoidDate>=@T0, RefundAmt=Paid, Paid/Tip/CloseDate unchanged, InUseID NULL, RefundOrders=0,
-- PayStatus=9, PayRefund=p.Amount, PayClose>=@T0 (cards), CCStatus=3, TxType=6, RAmt=p.Amount, CCTip=0, TipRows unchanged
SELECT OffDate, OffSent FROM Terminals WHERE TerminalID=@Term;                        -- NULL, NULL
SELECT QtyOnHand FROM Items WHERE ItemID=@I;                                          -- before + Qty (StockType<>2)
SELECT COUNT(*) FROM OrdersLog WHERE OrderID=@O AND Type IN (2,6);                    -- 0 (frmRefund logs nothing)
```

Variant — `OrderManager.VoidOrder` (only reachable by cancelling the eGift activation prompt at completion of an order that sells a gift card): expect additionally `Payments.RefundAmt=Amount` and `CloseDate` on **every** Completed/Auth payment including cash, `Items.QtyOnHand` bumped for every line **including pseudo-items and modifiers**, `FloorPlan.Status=6` with `OrderID` kept, a `GiftCards` row `FromRefund=1` when certificate/reward tenders were used, and no `OrdersLog` row; `Orders.CloseDate/Paid/Total` unchanged.

### 12.7 Void / delete an open order from the recall list

UI: Register → Get Order → select an Open order (no tab) → Void → confirm (reason prompt only when `RefReqReason`).

```sql
SELECT Status, ReasonID, Note, CloseDate, VoidDate, RefundAmt, InUseID FROM Orders WHERE OrderID=@O;
-- 5, 40 (or the chosen reason), NULL (or note), CloseDate>=@T0, VoidDate NULL, RefundAmt unchanged, InUseID unchanged
-- Orders.Note is a text column: compare it with CAST(Note AS varchar(max)) or LIKE, never with '=' on the column itself
SELECT Type, ID FROM OrdersLog WHERE OrderID=@O AND Type IN (2,6) ORDER BY Type;   -- (2, 1 in this DEBUG build / NULL in Release without an override), (6,@User)
SELECT COUNT(*) FROM Payments WHERE OrderID=@O AND Status=0;                     -- 0 (Open rows now 14; Completed rows keep 1)
SELECT QtyOnHand, QtySold FROM Items WHERE ItemID=@I;                             -- before + Qty, QtySold unchanged
SELECT Status, InUse, OrderID FROM FloorPlan WHERE OrderID=@O;                     -- no row (table now Status=6, OrderID NULL)
```

### 12.8 Full refund of a completed single-card order

UI: Refund → Find Order → `@O` → all lines checked (default) → Refund → (reason) → device Return → "Refund completed." `@R` = `SELECT MAX(OrderID) FROM Orders WHERE RefID=@O` (every partial refund of the same order adds another `RefID=@O` row, so never use a bare scalar subquery).

```sql
SELECT o.Status, o.RefundAmt, o.Paid, o.InUseID, r.OrderID, r.RefID, r.Status AS RStatus, r.Total, r.Paid AS RPaid, r.Tax, r.CloseDate, r.ReasonID, r.StaffID, r.TerminalID
FROM Orders o JOIN Orders r ON r.RefID=o.OrderID WHERE o.OrderID=@O;
-- exactly one row: o.Status=1, o.RefundAmt=o.Paid, o.InUseID NULL, RStatus=4, r.Total=r.Paid=-o.Paid, r.Tax=-o.Tax, CloseDate>=@T0, ReasonID=39 or chosen, StaffID=o.StaffID, TerminalID=@Term
SELECT p.PayID, p.Status, p.RefundAmt, np.Amount, np.Status AS NStatus, np.Type, np.OrderID, np.RefID, np.CloseUserID, c.TxType, c.Status AS CCStatus, c.Tip
FROM Payments p JOIN Payments np ON np.OrderID=@R AND np.Type=p.Type LEFT JOIN CCTxs c ON c.CCTxID=np.CCTxID
WHERE p.OrderID=@O AND p.Amount>0 AND p.Type NOT IN (2,5,15) AND p.Status<>14;   -- Coupon/Discount/ClubDiscount rows are hidden by frmRefund and never refunded
-- p.Status=1 (unchanged), p.RefundAmt=p.Amount; np.Amount=-p.Amount, NStatus=8, np.Type=p.Type, np.OrderID=np.RefID=@R, CloseUserID=@User; c.TxType=5, CCStatus=1
SELECT Type, ID FROM OrdersLog WHERE OrderID=@R ORDER BY Type;                       -- (0,@User),(1,@User),(2, 1 here / NULL in Release)
SELECT d.ODID, d.Qty, d.RefQty, m.Qty AS MirrorQty, m.RefQty AS MirrorRefQty
FROM OrdersDetails d JOIN OrdersDetails m ON m.OrderID=@R AND m.ItemID=d.ItemID AND m.Description=d.Description WHERE d.OrderID=@O AND d.Deleted=0;
-- d.RefQty=d.Qty, MirrorQty=-d.Qty, MirrorRefQty NULL  (the join cross-multiplies when the same item sits on two lines; add PriceSell to the ON clause then)
SELECT Type, Qty FROM OrdersTax WHERE OrderID=@R;                                     -- negated slot quantities
SELECT QtyOnHand, QtySold FROM Items WHERE ItemID=@I;                                 -- QtyOnHand before + Qty, QtySold unchanged
```

### 12.9 Partial refund (one unit of one line, cash order)

UI: as 12.8 but set the line's quantity to 1 (or uncheck other lines) → Refund.

```sql
SELECT o.Status, o.RefundAmt, o.Paid, r.Total, r.Paid AS RPaid, r.Status AS RStatus
FROM Orders o JOIN Orders r ON r.RefID=o.OrderID WHERE o.OrderID=@O;                -- 1, refunded part (< Paid), unchanged, -refunded part, same, 4
SELECT d.RefQty, d.Qty, m.Qty AS MirrorQty FROM OrdersDetails d JOIN OrdersDetails m ON m.OrderID=(SELECT MAX(OrderID) FROM Orders WHERE RefID=@O) AND m.ItemID=d.ItemID
WHERE d.ODID=@ODID;                                                                   -- 1, unchanged, -1
SELECT p.RefundAmt, np.Amount, np.Status, np.Type FROM Payments p JOIN Payments np ON np.OrderID=(SELECT MAX(OrderID) FROM Orders WHERE RefID=@O)
WHERE p.OrderID=@O AND p.Type NOT IN (2,5,15) AND p.Status<>14;                                                                   -- refunded part, -refunded part, 8, 0
-- a second partial refund creates a SECOND refund order (RefID=@O) and adds to RefundAmt/RefQty; a fully refunded line disappears from the refund grid
```

### 12.10 Pay Out (drawer)

UI: empty cart → Pay In/Out → Pay Out → reason row → amount 25.00 → note → OK.

```sql
SELECT o.OrderType, o.Status, o.Total, o.Tax, o.Paid, o.RefID, o.CloseDate, o.StaffID, o.TerminalID,
       p.Type AS PayType, p.Amount, p.Status AS PayStatus, p.Reference, p.BankID, p.RefID AS PayRefID, p.CloseDate AS PayClose,
       (SELECT COUNT(*) FROM OrdersLog l WHERE l.OrderID=o.OrderID) AS LogRows,
       (SELECT COUNT(*) FROM OrdersDetails d WHERE d.OrderID=o.OrderID) AS DetailRows
FROM Orders o JOIN Payments p ON p.OrderID=o.OrderID WHERE o.OrderID=@O;
-- 7, 1, 0, 0, NULL, @CustomID, >=@T0, @User, @Term; 9, -25.00, 1, <note>, @Bank or NULL, @CustomID, >=@T0; LogRows=2 (Types 0 and 1), DetailRows=0
-- Pay In: OrderType=6, PayType=8, Amount=+25.00
SELECT Cash, UserID, MngrID, Notes FROM BankMod WHERE BankID=@Bank AND Cash=-25.00 AND [Date]>=@T0;   -- only when @Bank is set: one row, @User, MngrID (1 here), Notes=<note>
-- (BankMod also receives shift-adjustment rows from frmBank.cs:290, so never assert on TOP 1 ORDER BY Date; Notes is a text column: use CAST(Notes AS varchar(max)) to compare)
```

### 12.11 Tip adjust after close (`frmReView`) and Complete Auth with tip

UI (adjust): Register → Complete → Review → select the card row → new tip → OK. `@P` = PayID, `@New` = new tip, `@Old` = previous tip (0 when none).

```sql
SELECT O.Paid, O.Tip, O.Total, P.Amount, P.Tip AS PayTip, C.Tip AS CCTip, C.TxType, T.Type AS TipType, T.Amount AS TipAmt, T.Value, T.StaffID
FROM Payments P JOIN Orders O ON O.OrderID=P.OrderID JOIN CCTxs C ON C.CCTxID=P.CCTxID LEFT JOIN OrdersTips T ON T.PayID=P.PayID
WHERE P.PayID=@P;
-- vs before: O.Paid/O.Tip/O.Total each +(@New-@Old); P.Amount +(@New-@Old); PayTip unchanged; CCTip=@New (NULL when 0); TxType=4;
-- TipType=1 with TipAmt=Value=@New when @Old was NULL; no OrdersTips row for @P when @New=0; StaffID=O.StaffID
```

UI (Complete Auth, restaurant, `Payments.Status=6`): Register → Complete → select the Authorized row → OK → tip prompt `@Tip`.

```sql
SELECT C.TxType, C.Status, C.CloseDate, C.Tip, P.Status AS PayStatus, P.Amount, P.CloseUserID, O.StaffID, O.Paid, O.Tip, O.Total, T.Type, T.StaffID AS TipStaff, T.Amount
FROM Payments P JOIN CCTxs C ON C.CCTxID=P.CCTxID JOIN Orders O ON O.OrderID=P.OrderID LEFT JOIN OrdersTips T ON T.PayID=P.PayID
WHERE P.PayID=@P;
-- C.TxType=1, C.Status=1, C.CloseDate NOT NULL, C.Tip=@Tip (NULL when 0), PayStatus=1, P.Amount=auth amount+@Tip (+@Tip again when Global.TouchScreen),
-- P.CloseUserID=O.StaffID, O.Paid/O.Tip/O.Total each +@Tip, T.Type=1 (7 with a CFD; never 6, even for an eGift auth), TipStaff=O.StaffID, T.Amount=@Tip
```

### 12.12 Open-ended cash refund and pay-at-table (device) rows

```sql
-- Refund → Open → amount @X → Cash → confirm
SELECT TOP 1 OrderID, Status, OrderType, RefID, ReasonID, StaffID, TerminalID, BaseAmount, Paid, Total, Tax, TDUsed, TFUsed, CloseDate, InUseID
FROM Orders ORDER BY OrderID DESC;   -- Status=10, OrderType=0, RefID NULL, ReasonID=39/chosen, @User, @Term, -@X, -@X, -@X, 0, 0, 0, >=@T0, NULL
SELECT Type, Status, Amount, CCTxID, OrderID, RefID, UserID, CloseUserID FROM Payments WHERE OrderID=@RefOrderID;  -- 0, 13, -@X, NULL, @RefOrderID, NULL, @User, @User
SELECT ItemID, CatID, SubCatID, Description, Price, PriceSell, Qty, RefQty FROM OrdersDetails WHERE OrderID=@RefOrderID;  -- 1, 1, 1, 'Open Ended Refund', @X, @X, -1, 1
SELECT COUNT(*) FROM OrdersTax WHERE OrderID=@RefOrderID;                                                             -- 0
-- Pay-at-table: rows with MachName='Clover Flex' must NOT appear unless a Flex device is deployed (open question 3)
SELECT p.Type, p.Status, p.MachName, p.BankID, p.TerminalID, o.PaidAtTable, o.Status FROM Payments p JOIN Orders o ON o.OrderID=p.OrderID WHERE p.PayID=@PayId;
-- 3, 1, 'Clover Flex', NULL, NULL, 1, 1 or 6
SELECT Type, Amount FROM OrdersTips WHERE PayID=@PayId;                                                               -- one row, Type=5, even for tip 0
```

## 13. Open questions / unresolved (deduplicated)

Items the slices raised that the code alone could not settle, merged across slices. Resolved while composing (not repeated below): a tender click on an unmodified recalled order **does** set `Modified` and run `SaveOrder` (csDataGridView.cs:927-933, csForm.cs:824); `Orders.InUseID` holds `Global.TerminalID`, not a user id (2034, 8925); stock is decremented inside `SaveOrder`, not at completion (2473-2485); the split/re-create/delivery `OrdersLog.Type` bindings are Create/Create/Close (19694, frmRefund.cs:3013, frmDelManager.cs:643); the completion batch does not touch `ItemTotal` (18637-18642); `SaveOrder`'s re-save UPDATE does rewrite `Orders.FPID` (2093); card-paid delivery orders are closed by the completion batch at tender time (18637, also when payments are Auth=6); A/R card tenders do write a `CCTxs` row referenced by `AccountPayments.CCTxId` (csPayment.cs:93-104 with `PayID=0`); `Security.Module.Main` = 20 (csSecurity.cs:28).

**Environment / build**
0. **No commit anchor for the app source.** `2.0.x`, `Exatouch.Entities` and `Exatouch.Repositories` are not git repositories on this machine; the line numbers are tied to the tree as read on 7 September 2026 (binary built 5 June 2026). Record a checksum or archive of the tree with any test that hard-codes these anchors.
1. **Which binary the QA lab runs.** The checked-in project only builds `DEBUG;CFD`: fake processor (random ~3 % declines, `TxRespons` NULL, `CardHolder='Fake credit card'`), no PIN gates and no "Manager PIN Request" `Log` rows, `MgrID=UserID=1`, drawer no-op, Training-style batch close, `frmOfflineProcess` unreachable. A Release/AUTOQA binary changes every processor-dependent expectation and adds the security modals.
2. **Tables absent from `db-schema.txt`**: `OrdersLog`, `Items`, `ItemsTx/ItemsTxDetails`, `Customers`, `Appointments`, `Bank/BankMod/BankDetails`, `CustomFields`, `Log`, `OpenPromoDetails/OrdersPromoDetails`, `FreshSheet`, `Staff`, `Config`, `Tax`, `CCTxFailures` (scout lists `CCtxFailures`; `Source` receives an enum at csPayment.cs:63 and a bit at :694, `CCNumber` a full PAN at :681 — truncation risk). Confirm columns, identity/defaults and names before asserting; the `OrdersLog(OLID, CDate, ID, OrderID, Type)` shape came from a one-off INFORMATION_SCHEMA read.
3. **External pay-at-table caller** (`spApplyPayment`/`spTipAdjust`, "Clover Flex") is not in the repository; if no device is deployed, rows with `MachName='Clover Flex'` must never appear.
4. **Runtime flags** that gate writes: `License.FloorPlanEnabled`, `Preference.Security.OpenAlwaysCD`, `Global.TouchScreen` (= `CFD.IsVGA`; switches `OrdersTips.Type` CC=1/CFD=7 and enables the double tip add), a real processor vs. `Global.IsConnected` (VoidOrder becomes local-only when disconnected), `Global.Tax.TaxID`, `Global.LocalSetting.BankID` (persisted in `Exatouch.cfg` — a bank swapped in during an earlier session still stamps `Payments.BankID`), `_OTSelected.ID` for built-in order types (`Orders.CustomID` expected NULL), `Register.Tip.Tips`, dual price.
5. `CustomFields` rows with `CustomID` 39 (refund reason), 40 (void), 43 (pay-out customer) are assumed to exist in every XTTDB.

**Register pay/complete**
6. Exact Cash stages a second Cash row (16430-16435): does a zero-amount `Payments` row remain after `SaveOrder`?
7. Double tip on touch-screen registers: `Complete(pID, null)` adds the tip to `Payments.Amount` at csPayment.cs:496-500 and again at 563-569 when `Global.TouchScreen` (non-multitouch order types such as Delivery/Custom; delivery-manager credit close, rows 65+70).
8. Tab Complete, non-touch, surcharge on and tender fee off: 17061 overwrites `Payments.Amount` with the in-memory amount (without the tip `Complete` just added) plus surcharge, and batch A omits `Amount` for tab rows — the tip may vanish from `Payments.Amount`.
9. `Orders.Total` keeps a CFD tip after the card is declined (only eGift reverses it, 19012-19035); confirm no later save rewrites it.
10. Certificate exchange rows (negative Certificate tender): `CustomValue.Description` null assigned to `mRow["SKU"]` at 18613 — `ArgumentNullException` on the UI thread vs. DBNull → `PriceOriginal` DBNull → `CreateGiftCertificate(0)` throwing on the background thread; needs a runtime check.
11. Real scenario producing `mDue<0` at 18307 (synthetic `Type=0, Amount<0` row plus a negative PayOutCust row) and the resulting `Orders.Paid`.
12. Reward tender on a $0 total divides by zero on the completion thread (18681, `mTotal` not refreshed after the DP rewrite).
13. `FloorPlan.StaffID` is nulled in memory (18917/9387) but never persisted by `DBUpdate`.
14. `_Payments.Compute("SUM([TFUsed])")` (12645) includes `Deleted=true` rows: intended fee suppression or a bug (visible after `_ResetPay` on a recalled order with fee rows)?
15. Which flow deletes the CRM INVOICE created at staging (15733) when `btnProcessClick` aborts for a reason other than a SaveOrder / `UpdateTransactionOrderId` failure.
16. `_CancelGiftCertificatePayment` (4655) also drops already-Completed certificate rows of a recalled order from the grid and `_Paid` while `[Payments]` keeps them.
17. Line 12000 adds the dual tender fee to the pre-tax I bucket with no `IsDualPrice` guard; whether a non-zero account tender fee can exist with dual price off (then `OrdersTax` Types 26-29 are over-stated). The dual-price formulas (`DualPriceOrderModel.TotalUpdate`, csDualPriceManager.cs:1031+) and the `IsDualPriceTriggerTender/ActiveTender` helpers were not traced; `GetDualPriceAmount` returning (0,0) on any exception makes `_Total = tip + other` silently.
18. Units of the dollar-per-unit tax buckets (`TaxBDollar/C/D/H/I`) and whether Tax A can be dollar-based; `TaxValue[i]` being fractions rests on frmTaxes.cs:259-262.
19. Tab Auth outside restaurant mode would send `Sale=2` and store `CCTxs.Status=1` before flipping to 2 — confirm the buttons exist only in restaurant mode.
20. The `csObject.Status` setter guard (csObject.cs:412) and `UpdateAll`: which table types/states make the new-order `FloorPlan` write keep the old Status/InUse, and whether the `Ordered` socket broadcast fires.
21. `Global.IsLock` semantics (csGlobal.cs:99) behind the early exits at 1759-1775; the UI path that reaches `_PrintOrder` at 2805 on a brand-new tab-auth order.
22. `Payments.BankID` when no drawer session exists: 16155 tests C# `null`, 16161 binds `?? DBNull` — confirm `BankMod` is skipped and `BankID` is NULL.

**Register other**
23. Existing-order ApprovedBy `OrdersLog` block (2138-2145) is dead: re-saving after a legal-age approval logs no `ApprovedBy=5` row — defect or intent?
24. Undo Exchange does not queue the label row's ODID (13876-13877): save → undo → save should leave a lingering `ItemID=19` row.
25. Removing the last line of a recalled order forces `Modified=false` and skips `Clear()` (13630-13635): when are the queued soft-deletes persisted and the lock released?
26. Split with a failing child INSERT (`@OrderID` NULL) — does SqlClient reject the null or do `OrdersLog/OrdersTax/OrdersDetails` rows with `OrderID NULL` appear? Split children do not inherit `Exchange/CustExempt/Surcharge/TDUsed/TFUsed` and the original's `Exchange` is not recomputed (19771) — intended?
27. Refund button with a never-saved modified cart and visible "No": frmRefund opens with the cart still in memory — what happens to it?
28. `Lib.JoinOrder` split-member branch (Lib.cs:5049-5056): with one split row left and `mFPID==pFPID` it deletes the **target's** `OrdersFPSplit` row and sets the table Ordered — intended collapse? `Payments` update at Lib.cs:5082 has no OrderID filter (DB-wide).
29. Recovery after the NRE at 9939 (failed `Printed=1`); `csPrint.OrderDetails` behaviour for `ReceiptAndRemote=4`/`StickyAgain=3`; other callers of `SearchOrder` (frmMain scan?).
30. `Global.Register.StaffID` vs `Global.UserID` divergence after floor-plan Take/Reassign (`Orders.StaffID` and the Create-row `ID` may differ from `Payments.UserID`) — by design after Reassign (19255); confirm for Take.

**Refund**
31. frmRefund write 14 (`FloorPlan.Status=6`) is judged dead from SqlClient semantics; confirm voiding a table order leaves `FloorPlan.Status` unchanged.
32. `Default.ResponseMessage` is only cleared on a decline (csPayment.cs:896); a stale message can make frmRefund.cs:2255 throw on a silently failed `VoidAuth` (disconnected, gift checks). Whether PAX/Dejavoo return the literal "already voided" on a re-void (OrderManager.cs:85) decides if a partially failed `VoidOrder` can be retried.
33. Zero-amount `Activate` at frmRefund.cs:2139 — any provider effect? eGift remote effects and the Training mock were not traced further.
34. `Orders.OtherRefunded` has no writer in the traced code (reader frmRefund.cs:323).
35. RefDays check at frmRefund.cs:395 never fires for past orders (only Find Order filters) — intended?
36. Offline + Debit/EBT open-ended refund falls through and writes `Payments.Type=4, CCTxID NULL` with no card transaction (1684-1688); offline Credit with a failed `SaveTrx` throws "parameter @CCTxID not supplied" at 1719 after offline mode was persisted; non-semi-integrated processors read `OCCNumber` (csPayment.cs:753) which the open-ended `CardData` never sets (NRE); what the lab processor returns for `Return` with `OrderID=0`; `frmMessageValue.MaxValue` cap semantics; `dgvDetailsCellEndEdit` clamp skipped for refund-without-receipt rows (3150).
37. Offline Return decline moves `Orders.Paid` the wrong way (`Paid-=ABS(Amount)`, frmOfflineProcess.cs:200); whether offline refunds are reachable.

**csPayment / Managers / satellites**
38. `Terminals.OffDate/OffSent` are cleared on **every** online `VoidAuth` (csPayment.cs:1173-1181) and every non-offline voided row in frmRefund (2846) — intended, or hides pending offline transactions?
39. `Complete` for gift/offline payments overwrites `CCTxs.PNRef`/`TransmitID` with '' (csPayment.cs:483-491); whether a live system reaches it with an offline row.
40. Dapper full-row round trips of `CCTxs.Number` (varbinary) and `Sign` (image) in `VoidOrder`/`UpdateNumber`: confirm a voided card row keeps its `Sign` bytes.
41. `OrderManager.VoidOrder` bumps `QtyOnHand` of pseudo-items (ItemID 1-12, StockType=2) and modifiers (no StockType filter) whereas frmRefund and frmGetOrders filter `StockType<>2` — reporting impact unknown.
42. Delivery cash completion adds `frmComplete.Tip` (prefilled from `Orders.Tip`, read-only for online orders) to `Orders.Total/Paid` and `Payments.Amount` — an order that already carried a tip has it counted twice unless cleared; also no drawer/receipt call on that branch.
43. The path after "Ignore" on a wrapped `ExecReader` (null reader → `.HasRows` NRE) is inferred, not observed.

