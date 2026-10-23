# Capturo

Captura de tela e gravação de vídeo para Windows, com editor de anotações. Feito em C# / .NET 8 / WPF.

## Capturas

| Ação | Atalho padrão |
| --- | --- |
| Capturar região (seleção sobre a tela congelada, clique numa janela, Enter = monitor) | **Win+Shift+W** |
| Capturar região (alternativo) | Print |
| Capturar a última região | Shift+Print |
| Capturar tela cheia (monitor do cursor) | Ctrl+Alt+Shift+Print |
| Capturar todas as telas · janela ativa · com atraso · com rolagem automática | menu da bandeja (atalho opcional) |
| Reconhecer texto (OCR do Windows) · ler QR Code/código de barras · seletor de cor | menu da bandeja (atalho opcional) |
| Gravar vídeo da tela cheia / de uma região (o mesmo atalho para a gravação) | Ctrl+Print / Ctrl+Shift+Print |

**Todos os atalhos são configuráveis** em *Configurações…* (menu da bandeja): clique no campo e pressione a combinação; Backspace deixa a ação sem atalho. Atalhos repetidos ou já usados por outro programa ficam marcados em vermelho.

> Se o Screenpresso (ou outro capturador) estiver aberto, ele segura os mesmos atalhos e o Capturo avisa que estão em uso. Feche o outro programa ou escolha outras combinações.

Linha de comando: `Capturo.exe --action CaptureRegion` (qualquer ação da lista em `Services/Hotkey.cs`), `--capture`, `--settings`. Com o Capturo já aberto, o comando é repassado para ele.

## Editor

- **Ferramentas**: mover (V), seta (A), retângulo (R), elipse (E), linha (L), caneta (P), marca-texto (H), texto (T), balão de texto (K), passo numerado (N), **destaque** (S: escurece tudo fora da área), **lupa** (M: amplia uma área numa lente ao lado), ocultar/pixelizar (B), recortar (C).
- **Tudo continua editável**: o item desenhado fica selecionado com alças para redimensionar; arraste para mover; setas do teclado ajustam (Shift = 10 px); duplo clique ou Enter reabre o texto; Delete apaga. Selecionar um item mostra o estilo dele na barra.
- **Estilo**: 9 cores, tamanho, preenchimento, cantos arredondados e opacidade (no destaque, a opacidade controla o escuro em volta).
- **Imagem**: borda liga/desliga com cor e espessura, sombra projetada, cantos arredondados e borda rasgada (embaixo, embaixo+direita ou todos os lados). A última escolha é lembrada.
- Desfazer/refazer ilimitado, zoom 1:1 com pixels físicos, copiar (bitmap + PNG), salvar PNG/JPEG.

## Vídeo

MP4 (H.264) pelo codificador do Windows, com aceleração de hardware quando disponível. Desenha o cursor e destaca cliques (amarelo: esquerdo, vermelho: direito). Barra flutuante com tempo, pausar, parar e descartar; a moldura e a barra não aparecem no vídeo. Áreas maiores que 3840 × 2160 são reduzidas proporcionalmente. Ainda sem áudio.

## Compilar e rodar

O SDK foi instalado de forma portátil em `%LOCALAPPDATA%\dotnet-sdk` (sem admin):

```powershell
$dn = "$env:LOCALAPPDATA\dotnet-sdk\dotnet.exe"
& $dn build Capturo.sln
& $dn test Capturo.sln
& .\src\Capturo\bin\Debug\net8.0-windows10.0.19041.0\Capturo.exe
```

Configurações e log ficam em `%APPDATA%\Capturo`; capturas em `Imagens\Capturo` (configurável).

## Estrutura

```
src/Capturo
  Capture/   ScreenGrabber, WindowFinder, CaptureSession + OverlayWindow (seleção por monitor),
             ScrollingCapture (rolagem + costura), Recognition (OCR e QR), janelas de contagem e resultado
  Editor/    Annotations (registros imutáveis com alças), ImageEffects, EditorHistory, AnnotationRenderer, EditorWindow
  Video/     VideoRecorder (BitBlt → MediaStreamSource → MediaTranscoder), moldura e barra de gravação
  Services/  AppSettings, Hotkey/HotkeyManager/HotkeyBox, SettingsWindow, CommandChannel, OutputService, TrayIcon, Log
tests/Capturo.Tests   geometria, anotações, histórico, atalhos, costura da rolagem, OCR e QR
```

## Ainda não feito

- Áudio (microfone e som do sistema) na gravação.
- Webcam sobreposta ao vídeo, legendas automáticas e conversão para GIF/WebM.
- Workspace (histórico de capturas com miniaturas) e compartilhamento em nuvem.
- Vídeo em tela muito grande (5120 px) fica em ~8 fps com BitBlt; usar Windows.Graphics.Capture resolveria.
