# Resultado de la POC: FlaUI (open source) vs Ranorex — sobre Exatouch real

Fecha: 2026-06-08. Test ejecutado contra `bin\Exatouch.exe` real, en paralelo al run del
fin de semana (sin afectarlo). Proyecto: `qa-ai/flaui-poc/` (FlaUI + NUnit, todo NuGet gratis).

## Qué se probó
`Test1_FlaUI_puede_ver_el_arbol_de_controles_de_Exatouch`: lanzar Exatouch con FlaUI y
volcar el árbol de UI Automation. Es la prueba decisiva de viabilidad: si UIA ve los
controles, FlaUI sirve y el agente puede generar tests sin licencia Ranorex.

## Resultado: PASÓ ✓ — FlaUI es viable para Exatouch

- **141 controles** visibles para UI Automation en la pantalla principal.
- Botones del menú con **AutomationId = Name real del Designer** y texto legible:
  `btnRegister`(Register), `btnItems`(Items), `btnPurchasing`(Purchasing),
  `btnSettings`(Settings), `btnReports`, `btnStaff`, `btnCustomers`, `btnUtil`.
- FlaUI atravesó incluso los sub-forms MDI: `frmMenu`, `frmRegister` con sus controles.
- Duración: 10 s. Sin licencia. Sin instrumentación especial.

## Matiz honesto (lo que NO es perfecto)
Algunos controles custom (los `csButton`/paneles con dibujo `OnPaint` — 47 en el código)
aparecen como `[Pane]` con **AutomationId numérico autogenerado** (`2362868`, `920946`) en
vez de su Name. Esos son localizables por **texto** (`ByName("Find")`) o jerarquía, pero son
menos robustos que por AutomationId. Estimación: ~80% de controles ideales por AutomationId,
~20% requieren localización por texto/posición. Aquí es donde Ranorex (instrumentación
profunda) tendría ventaja — pero la mayoría de Exatouch NO la necesita.

## Comparación final para Exatouch

| Criterio | Ranorex | FlaUI (open source) |
|---|---|---|
| Costo | Licencia comercial | Gratis (MIT) |
| Ve los controles de Exatouch | Sí | **Sí — 141 confirmados** |
| Localiza por nombre, no coordenadas | RanoreXPath | AutomationId/Name (confirmado) |
| Controles custom dibujados (~20%) | Mejor (instrumentación) | Por texto/posición (funciona, menos robusto) |
| Grabador | Sí (Spy + recorder) | No — el **agente genera el código** |
| Tests autogenerables por IA | Limitado (needs-recording) | **Sí — el agente lee Designer.cs y escribe el locator** |
| Ya invertido | 135 grabaciones de QA | proyecto nuevo |

## Conclusión
FlaUI **sirve para Exatouch** y permite el camino "IA como grabador": el agente lee el
`Designer.cs`, ve `btnPurchasing`/`txtName`, y escribe el test C# ejecutable — confirmado que
esos controles existen en el árbol UIA en runtime. Recomendación: usar el trial de Ranorex
para aprovechar las 135 grabaciones existentes en el corto plazo, y FlaUI como camino
sostenible sin licencia para los tests nuevos que el agente genera (los `needs-recording`).

## Reproducir
```powershell
cd C:\Dev\exatouch-qa\flaui-poc
dotnet test --filter Test1_FlaUI_puede_ver_el_arbol_de_controles_de_Exatouch
```
