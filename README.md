# Traductor NWN:EE (EN/ES)

Herramienta en C# (.NET 8, WinForms) para traducir en tiempo real el chat de
Neverwinter Nights: Enhanced Edition, inspirada en la idea del traductor que
ya usabas, pero escrita desde cero para tu propio juego.

## Qué hace

- Vigila el archivo de log `nwclientlog1.txt` (en `OneDrive\Documents\Neverwinter Nights\logs`
  o `Documents\Neverwinter Nights\logs`) y traduce cada línea nueva de inglés a español,
  mostrando ambos textos en una ventana tipo chat.
- Permite escribir en español en un cuadro de texto; al pulsar Enter, traduce
  el texto a inglés y lo envía automáticamente a la ventana del juego (simulando
  el Enter + escritura + Enter que usa el chat de NWN).
- Traduce con el endpoint público de Google Translate por defecto. Si introduces
  una API Key de DeepL en el panel inferior, usa DeepL en su lugar (más preciso,
  tiene plan gratuito).

## Cómo generar el .exe (tú, en tu PC con Windows)

1. Si no tienes el SDK de .NET 8, descárgalo (gratis) de
   https://dotnet.microsoft.com/download/dotnet/8.0 (elige "SDK").
2. Copia esta carpeta completa a tu PC.
3. Haz doble clic en `build.bat`.
4. Cuando termine, el ejecutable estará en `dist\TraductorNWN.exe`.
   Es un único archivo portable: no necesita tener .NET instalado para
   ejecutarse, y puedes moverlo a cualquier carpeta o llevarlo en un USB.

## Notas

- La primera vez que lo abras, Windows Defender/SmartScreen puede avisar porque
  el ejecutable no está firmado digitalmente (firmar cuesta dinero y no es
  necesario para uso personal). Dale a "Más información" → "Ejecutar de todas formas".
- Si el juego usa un título de ventana distinto en tu instalación, edita en
  `Program.cs` la función `SendToGame` y ajusta el texto que se busca con
  `FindWindow`.
- El traductor de Google que usa esta app es un endpoint público no oficial;
  funciona bien para uso personal pero puede fallar si Google cambia algo.
  DeepL es más estable si tienes cuenta gratuita.
