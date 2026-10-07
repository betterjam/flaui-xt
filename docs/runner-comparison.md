# Ranorex vs FlaUI vs "algo como Playwright" — para Exatouch (WinForms)

Decisión: qué motor de **ejecución** de tests E2E usar. El catálogo (`qa-ai/catalog/`) es
agnóstico del runner por diseño (cada caso tiene campo `runner`), así que esta decisión
**no afecta** la discovery, los anchors, la cobertura ni el INDEX — solo la capa que corre
los tests contra la app.

## Aclaración clave: Playwright no sirve aquí
Playwright/Selenium/Cypress automatizan **navegadores vía DOM**. Exatouch es **WinForms de
escritorio**: no hay DOM, no hay página. Esas herramientas no pueden ver la app. Lo que
buscamos es el *equivalente de escritorio* de la experiencia Playwright.

## Las opciones reales para WinForms

| Criterio | **Ranorex Studio** | **FlaUI** (open source) | WinAppDriver / Appium |
|---|---|---|---|
| Licencia | Comercial (cara) | MIT — gratis | Gratis (Microsoft, semi-abandonado) |
| Cómo localiza controles | RanoreXPath + coordenadas grabadas | UI Automation (`AutomationId`/nombre/tipo) | UI Automation (igual) |
| **Grabador / codegen** | ✅ Sí (Spy + recorder) | ❌ No (hay FlaUInspect para inspeccionar) | ❌ No |
| Tests como código | Sí (C#) + grabaciones | **Sí, C# puro** | C#/cualquier lenguaje Appium |
| ¿El agente IA puede generar tests ejecutables? | ⚠️ Limitado: no inventa locators sin la app corriendo → quedan `needs-recording` | ✅ **Sí**: busca por AutomationId/nombre, que el agente lee del código/Designer | ✅ Sí (similar a FlaUI) |
| Headless / CI | Sí (Test Suite Runner) | Sí | Sí |
| Auto-wait / retries | Sí, maduro | Básico (`Retry`, waits manuales) | Básico |
| Madurez / soporte | Alta, soporte comercial | Activo, comunidad | Bajo mantenimiento |
| Lo que ya tenés | 135 grabaciones + suites de QA | nada aún | nada |

## El trade-off central
- **Ranorex**: tiene grabador (bueno para humanos) pero cuesta licencia, y el agente IA
  **no** puede autogenerar los locators → muchos tests quedan `needs-recording` (QA graba).
- **FlaUI**: gratis y los tests son código que el **agente SÍ puede escribir** (busca
  `AutomationId`/nombre, que se leen del Designer.cs). Pierde el grabador — pero el agente
  *reemplaza* al grabador. Resultado: menos `needs-recording`, más tests directamente
  ejecutables. Es lo más cercano a "la experiencia Playwright" para escritorio.

## Por qué FlaUI encaja con el sistema que ya construimos
1. Nuestro catálogo es runner-agnóstico: agregar `runner: flaui` y un prompt que genere C#
   + FlaUI reutiliza TODO (discovery, anchors, coverage, ledger, INDEX, weekend-runner).
2. El "seam" de Ranorex (agente no puede grabar locators) desaparece: FlaUI localiza por
   AutomationId/Name, que el agente extrae del código WinForms. Tests ejecutables, no planes.
3. Requisito previo importante: los controles WinForms necesitan `AccessibleName`/`Name`
   estables para localización robusta (igual que `data-testid` en web). Si faltan, hay que
   agregarlos en la app — PERO eso toca código de la app (prohibido por la regla "never touch
   core code"), así que se haría como tarea aparte, revisada, no por el agente de tests.

## Recomendación
- **Corto plazo / esta máquina**: probá el **trial de Ranorex** y aprovechá las 135
  grabaciones existentes de QA. Cero trabajo nuevo, valida el flujo hoy.
- **Mediano plazo / para escalar sin licencias**: prueba de concepto con **FlaUI** — un test
  C# ejecutable para un anchor simple, generado por el agente. Si el agente produce FlaUI que
  corre de verdad, es el camino sostenible (gratis + autogenerable + ~"como Playwright").
- **Evitar**: WinAppDriver como apuesta principal (poco mantenido); White (descontinuado);
  Playwright/Selenium (son web, no aplican).

## Riesgo a tener presente (vale para CUALQUIER runner)
Las grabaciones/tests de UI dependen de la resolución y del estado de la app. Exatouch en
build CFD/Release abre en **fullscreen borderless**; conviene el build en ventana 1024×768
para grabar/ejecutar, o los locators por coordenadas (Ranorex) fallan. FlaUI por
AutomationId es más robusto ante esto, pero igual necesita la app en un estado conocido.
