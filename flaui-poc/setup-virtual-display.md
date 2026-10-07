# Monitor virtual (headless) para correr tests FlaUI sin monitor físico

## Por qué (y la verdad técnica)
WinForms + UI Automation **no corre 100% headless** como Playwright: necesita un escritorio
de Windows que dibuje ventanas. La solución es una **pantalla virtual** — un driver de
monitor que crea un display real en software, sin hardware. Los tests corren ahí; tu monitor
físico (5120×1440) queda libre. Bonus: fijás 1024×768, la resolución que Exatouch espera, así
los tests son estables (recordá que en pantallas anchas los tests por coordenadas fallan).

## Opción elegida: IddSampleDriver (open source, Microsoft)
Indirect Display Driver de ejemplo de Microsoft, usado ampliamente como monitor virtual.

### Instalación (requiere ADMIN — correr vos, no el agente)

1. **Descargar** el driver compilado desde un release confiable:
   - Repo: https://github.com/ge9/IddSampleDriver  (fork mantenido con instalador)
   - O el original de Microsoft: https://github.com/roshkins/IddSampleDriver
   - Descomprimir en `C:\IddSampleDriver\`

2. **Permitir drivers de prueba** (el driver no está firmado por WHQL). En PowerShell admin:
   ```powershell
   bcdedit /set testsigning on
   # reiniciar la máquina para que tome efecto
   ```
   ⚠️ Esto baja una protección de drivers. Aceptable en una máquina de build/QA; valorá el
   riesgo en una máquina personal.

3. **Instalar el driver** (PowerShell admin, en la carpeta del driver):
   ```powershell
   .\installCert.bat        # instala el certificado de prueba
   # luego, en Device Manager: Add legacy hardware -> Display adapters ->
   #   Have Disk -> apuntar a IddSampleDriver.inf
   ```

4. **Configurar la resolución a 1024×768**: editar `option.txt` del driver:
   ```
   1
   1024, 768, 60
   ```
   (1 monitor, 1024×768 @ 60Hz)

5. **Verificar** que aparece el monitor virtual:
   ```powershell
   Add-Type -AssemblyName System.Windows.Forms
   [System.Windows.Forms.Screen]::AllScreens | ForEach-Object { "$($_.DeviceName) $($_.Bounds.Width)x$($_.Bounds.Height)" }
   ```
   Deberías ver el ultrawide + un `1024x768` nuevo.

### Correr los tests EN la pantalla virtual
Para que Exatouch abra en el monitor virtual (no el físico), el test fuerza la posición de la
ventana al área del monitor virtual. El helper `FlaUiTestBase` puede setear
`window.Move(virtualScreen.Bounds.X, virtualScreen.Bounds.Y)` tras lanzar la app
(se agrega cuando el monitor virtual esté instalado y sepamos sus coordenadas).

## Alternativa sin instalar driver (si testsigning no es opción)
- **Sesión RDP desconectada**: conectarse por RDP y cerrar el cliente SIN logout mantiene el
  escritorio "vivo" en una resolución que vos elegís en el cliente RDP. Cero instalación.
  Contra: la sesión de consola se bloquea mientras la RDP está activa.

## Estado
- [ ] Driver instalado (requiere tu acción admin + reinicio)
- [ ] option.txt = 1024×768
- [ ] helper actualizado para mover la ventana al monitor virtual
