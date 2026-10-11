# Traductor NWN:EE (EN/ES)

Herramienta en C# (.NET 8, WinForms) para traducir en tiempo real el chat de
Neverwinter Nights: Enhanced Edition.

## Qué hace

- Vigila el log `nwclientLog1.txt` (en tu carpeta Documentos, también si está
  redirigida a OneDrive) y traduce los mensajes nuevos de inglés a español.
- Tres pestañas: **Ambos** (traducción y debajo el original), **Inglés** (al
  instante, sin esperar a la traducción) y **Español**. `Ctrl+1/2/3` cambia de pestaña.
- Escribes en español, pulsas Enter (o el botón de enviar) y se traduce al inglés,
  se escribe en el chat del juego y se envía solo. Tus mensajes aparecen a la
  derecha: "✓ enviado" cuando el juego lo confirma en su log, "sin confirmar" si
  no aparece (entonces pulsa Enter en el juego) o "no enviado" si falló.
- El envío simula el teclado con códigos de tecla reales (no `SendKeys`), así el
  juego reconoce el Enter. El texto viaja como caracteres Unicode: llega bien con
  cualquier distribución de teclado (ñ, tildes, ¿ ¡) y nunca dispara atajos del juego.
- Traduce con Google Translate o con DeepL (tiene plan gratuito).

## Escribir en el juego

- **Enter** traduce y envía. **Ctrl+Enter** envía el texto tal cual, sin traducir
  (nombres, frases que ya tienes en inglés, comandos).
- **Esc** cancela el mensaje que está esperando a salir y lo devuelve al campo
  para corregirlo (para eso sirve la espera de "Esperar antes de enviar").
- **Comandos de chat**: `/p`, `/s`, `/w`, `/dm`, `/tk` y `/t "Nombre"` se mantienen
  y solo se traduce el mensaje: `/t "Seraphine" nos vemos luego` → `/t "Seraphine" see you later`.
  Los demás comandos (`/roll`, `!comando` del servidor…) se envían tal cual.
- Tras enviar, vuelve al traductor para seguir escribiendo (se puede desactivar
  en Ajustes → "Después de enviar").
- Las comillas tipográficas, guiones largos, emojis y demás caracteres que NWN no
  sabe mostrar se cambian por su equivalente simple o se quitan.
- Si no se pudo traducir o enviar, el mensaje vuelve al campo de texto.

## La interfaz

- **El chat se lee como una crónica**: hora, nombre en el color de acento,
  mensaje en letra serif y el original debajo, más pequeño. Las acciones entre
  asteriscos (`*se sienta*`) salen en cursiva y en otro color.
- **Etiquetas de canal**: privado, susurro, grupo, grito, DM y servidor. Los
  mensajes que no se pudieron traducir a tiempo llevan la etiqueta "sin traducir".
- **Indicador de estado** arriba a la derecha: traductor activo, mensajes en
  cola y la pausa en segundos cuando la API está limitando.
- **Ajustes** (botón de la cabecera, `Esc` para cerrar): traductor, clave de
  DeepL con comprobación y consumo del mes, espera antes de enviar, qué se
  traduce, colores y "siempre encima".
- **Cuatro paletas de color**: Taberna (madera y latón), Pizarra (gris azulado
  y naranja), Arcano (índigo y verde azulado) y Bosque (verde pino y oro).
- Barra de título del color del tema en Windows 11 y barras de desplazamiento
  oscuras. Fuentes Sitka y Bahnschrift, que vienen con Windows.
- Todos los controles se dibujan a mano: ya no hace falta Guna ni FontAwesome.

## Muchos mensajes y bloqueos de la API

- **Agrupa** todas las líneas que llegan juntas en una sola petición.
- **Caché**: los mensajes repetidos no vuelven a pedirse; tu propio mensaje,
  cuando el juego lo escribe en el log, se muestra en español sin gastar petición.
- Si la API devuelve 429 (límite de peticiones), se hace una **pausa con espera
  progresiva** (respetando `Retry-After`). Mientras tanto los mensajes se van
  acumulando y salen todos juntos al acabar la pausa.
- Si un mensaje lleva más de 15 s esperando, se muestra **sin traducir** para que
  el chat no se quede atrás respecto al juego.
- **Solo el chat de jugadores** (activado por defecto): el combate y los mensajes
  del sistema aparecen sin traducir en la pestaña Inglés. Es lo que más
  peticiones ahorra en las peleas.
- Si DeepL se queda sin cuota o la clave falla, sigue con Google automáticamente.
- Al empezar una partida nueva (el juego rota el log) ya **no** se vuelve a
  traducir la sesión anterior entera.

## Cómo generar el .exe

**Sin instalar nada:** GitHub lo compila solo. Cada vez que cambia el código en
`master`, el flujo *Compilar exe* (pestaña **Actions**) genera
`dist\TraductorNWN.exe` y lo guarda en el repositorio. También se puede lanzar a
mano desde Actions → Compilar exe → *Run workflow*.

**En tu PC:**

1. Instala el SDK de .NET 8: https://dotnet.microsoft.com/download/dotnet/8.0 (elige "SDK").
2. Haz doble clic en `build.bat`.

En ambos casos el ejecutable es un único archivo portable que no necesita .NET
instalado.

## Configuración

Se guarda en `%AppData%\TraductorNWN\config.json` (todo se cambia desde Ajustes):

- `DeepLKeyCifrada`: la clave de DeepL, cifrada para tu usuario de Windows (la
  versión anterior la guardaba en texto plano junto al .exe; se migra sola).
- `Proveedor`: `Google` o `DeepL`.
- `Tema`: `taberna`, `pizarra`, `arcano` o `bosque`.
- `Mayusculas`, `FiltrarSistema`, `SiempreEncima`.
- `RetardoEnvioMs`: espera antes de escribir tu mensaje en el juego (0 a 5000; 2000 por defecto).
- `Ventana`: posición y tamaño de la ventana.

Los errores inesperados se registran en `%AppData%\TraductorNWN\errores.log`.

## Notas

- **El juego tiene que guardar el chat en su log.** En NWN:EE se activa en
  `Documentos\Neverwinter Nights\settings.tml`, sección `[game.log.chat.all]`,
  con `enabled = true` (con el juego cerrado). Deja `[game.log.chat.text]` en
  `enabled = false` o cada mensaje saldrá dos veces. Si no está activado, el
  traductor te avisa al arrancar.

- Windows SmartScreen puede avisar porque el .exe no está firmado:
  "Más información" → "Ejecutar de todas formas".
- La ventana del juego se busca por el proceso `nwmain.exe` y, si no, por título
  (sin confundirla con la propia ventana del traductor ni con navegadores).
- Si el juego se ejecuta como administrador, el traductor también tiene que
  ejecutarse como administrador para poder escribir en él.
- El endpoint de Google es público y no oficial; DeepL es más estable si tienes
  cuenta gratuita.
