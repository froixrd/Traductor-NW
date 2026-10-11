# Traductor NWN:EE (EN/ES)

Herramienta en C# (.NET 8, WinForms) para traducir en tiempo real el chat de
Neverwinter Nights: Enhanced Edition.

## Qué hace

- Vigila el log `nwclientLog1.txt` (en tu carpeta Documentos, también si está
  redirigida a OneDrive) y traduce los mensajes nuevos de inglés a español.
- Tres pestañas: **Ambos** (traducción y debajo el original), **Inglés** (al
  instante, sin esperar a la traducción) y **Español**. `Ctrl+1/2/3` cambia de pestaña.
- Escribes en español, pulsas Enter (o el botón de enviar) y se traduce al inglés
  y se escribe en el chat del juego. Tus mensajes aparecen a la derecha, marcados
  como enviados o no enviados.
- Traduce con Google Translate o con DeepL (tiene plan gratuito).

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

1. Instala el SDK de .NET 8: https://dotnet.microsoft.com/download/dotnet/8.0 (elige "SDK").
2. Haz doble clic en `build.bat`.
3. El ejecutable queda en `dist\TraductorNWN.exe` (un único archivo portable, no
   necesita .NET instalado).

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

- Windows SmartScreen puede avisar porque el .exe no está firmado:
  "Más información" → "Ejecutar de todas formas".
- La ventana del juego se busca por el proceso `nwmain.exe` y, si no, por título
  (sin confundirla con la propia ventana del traductor ni con navegadores).
- Si el juego se ejecuta como administrador, el traductor también tiene que
  ejecutarse como administrador para poder escribir en él.
- El endpoint de Google es público y no oficial; DeepL es más estable si tienes
  cuenta gratuita.
