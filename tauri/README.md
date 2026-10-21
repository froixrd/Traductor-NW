# Traductor NWN 3 (versión Tauri)

La misma herramienta que la versión de C#, rehecha con una interfaz web (React) dentro de una
ventana nativa y el núcleo en Rust. Un solo `.exe` portable, sin instalar nada.

| | Versión C# (WinForms) | Esta versión |
|---|---|---|
| `.exe` | ~65 MB | unos pocos MB |
| Memoria | ~60–100 MB | menos (ver `dist/analisis-tauri.txt`) |
| Interfaz | controles dibujados a mano | HTML y CSS: fácil de cambiar |

Usa el **WebView2** que ya trae Windows 10 y 11. Si en algún Windows antiguo no está, se
descarga gratis de Microsoft (*WebView2 Runtime*).

## Qué cambia al usarla

- Comparte la configuración con la versión anterior (`%AppData%\TraductorNWN\config.json`): las
  claves de Microsoft y DeepL, la paleta y las palabras que no se traducen se mantienen.
- Ventana sin bordes con su propia barra: chincheta para "siempre encima", ajustes en un panel lateral.
- Cada jugador tiene una ficha y un color fijos según su nombre.
- Tu mensaje aparece en cuanto pulsas Enter y su estado cambia en el sitio: traduciendo, en cola,
  a punto de salir (con una mecha que se consume durante la espera; Esc la corta), escribiendo en
  el juego, enviado o sin confirmar.
- Lo que llega del juego aparece al momento con "traduciendo…" y se completa al llegar la traducción.
- Si estás leyendo más arriba, no te mueve: aparece un aviso con los mensajes nuevos.
- Al escribir `/p`, `/t "Nombre"`, `!comando`… se ve qué canal se va a usar.

Todo lo demás funciona igual: los tres traductores con respaldo, caché y pausas, las abreviaturas,
los comandos de chat, Ctrl+Enter, Esc, la tecla rápida Ctrl+Mayús+Espacio, el aviso de privados,
la ventana compacta y la transparencia.

## Cómo se genera el .exe

GitHub lo compila solo con el flujo **Compilar exe (Tauri)** cada vez que cambia algo en
`tauri/` de esta rama, y lo guarda en `dist/TraductorNWN-tauri.exe` junto con
`dist/analisis-tauri.txt` (prueba de arranque, memoria y Microsoft Defender) y una captura.

En tu PC (opcional): Node.js 22, Rust y, dentro de `tauri/`, `npm ci` y `npx tauri build --no-bundle`.

## Partes

- `src/`: la interfaz (React). `npm run build:demo` genera una vista previa con una partida de ejemplo.
- `nucleo/`: el núcleo en Rust (log del juego, traducción, cola, envío, ajustes), con pruebas
  (`cargo test -p nucleo`), incluida una de principio a fin con un log real y un juego falso.
- `src-tauri/`: la ventana y lo propio de Windows (escribir en el juego, tecla rápida, avisos).
