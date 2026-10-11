# Traductor NWN:EE (EN/ES)

Herramienta en C# (.NET 8, WinForms) para traducir en tiempo real el chat de
Neverwinter Nights: Enhanced Edition.

## Qué hace

- Vigila el log `nwclientLog1.txt` (en tu carpeta Documentos, también si está
  redirigida a OneDrive) y traduce los mensajes nuevos de inglés a español.
- Tres pestañas: **Ambos** (traducción + original), **Inglés** (al instante, sin
  esperar a la traducción) y **Español**.
- Escribes en español, pulsas Enter y se traduce al inglés y se escribe en el
  chat del juego (Enter + texto + Enter).
- Traduce con Google Translate por defecto; con una clave de DeepL (tiene plan
  gratuito) usa DeepL.

## Muchos mensajes y bloqueos de la API

- **Agrupa** todas las líneas que llegan juntas en una sola petición.
- **Caché**: los mensajes repetidos no vuelven a pedirse; tu propio mensaje,
  cuando el juego lo escribe en el log, se muestra en español sin gastar petición.
- Si la API devuelve 429 (límite de peticiones), se hace una **pausa con espera
  progresiva** (respetando `Retry-After`). Mientras tanto los mensajes se van
  acumulando y salen todos juntos al acabar la pausa. El indicador de arriba a la
  derecha muestra la pausa y los mensajes en cola.
- Si un mensaje lleva más de 15 s esperando, se muestra **sin traducir** para que
  el chat no se quede atrás respecto al juego.
- Botón de **filtro** (al lado de mayúsculas, activado por defecto): solo se
  traduce el chat de jugadores. El combate y los mensajes del sistema aparecen sin
  traducir en la pestaña Inglés. Es lo que más peticiones ahorra en peleas.
- Si DeepL se queda sin cuota o la clave falla, sigue con Google automáticamente.
- Al empezar una partida nueva (el juego rota el log) ya **no** se vuelve a
  traducir la sesión anterior entera.

## Cómo generar el .exe

1. Instala el SDK de .NET 8: https://dotnet.microsoft.com/download/dotnet/8.0 (elige "SDK").
2. Haz doble clic en `build.bat`.
3. El ejecutable queda en `dist\TraductorNWN.exe` (un único archivo portable, no
   necesita .NET instalado).

## Configuración

Se guarda en `%AppData%\TraductorNWN\config.json`:

- `DeepLKeyCifrada`: la clave de DeepL, cifrada para tu usuario de Windows (la
  versión anterior la guardaba en texto plano junto al .exe; se migra sola).
- `Mayusculas`, `FiltrarSistema`: estado de los dos botones.
- `RetardoEnvioMs`: espera antes de escribir tu mensaje en el juego (2000 por defecto).
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
