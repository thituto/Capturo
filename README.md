# Capturo

Captura de tela para Windows com editor de anotações. Feito em C# / .NET 8 / WPF, sem dependências de runtime além do .NET Desktop Runtime 8.

## O que faz (v0.1)

- **Seleção sobre a tela congelada**, uma janela de overlay por monitor, em pixels físicos (per-monitor DPI v2, monitores com escalas diferentes e coordenadas negativas).
- **Detecção de janelas**: passe o mouse e clique para capturar a janela inteira (usa a moldura real do DWM, sem a borda invisível do Windows 11).
- **Lupa** com pixel central, cor em hex e coordenadas; **C** copia a cor.
- **Editor**: seta, retângulo, elipse, linha, caneta, marca-texto, texto, passos numerados, pixelização de dados sensíveis e recorte. Mover, recolorir e redimensionar itens, desfazer/refazer ilimitado, zoom 1:1 com pixels físicos.
- **Saída**: salva automaticamente em `Imagens\Capturo`, copia para a área de transferência (bitmap + PNG) e exporta PNG/JPEG.

## Atalhos

| Onde | Tecla | Ação |
| --- | --- | --- |
| Global | Print ou Ctrl+Alt+R | Capturar região |
| Global | Shift+Print | Capturar o monitor atual |
| Global | Ctrl+Print | Capturar todas as telas |
| Seleção | Enter / Espaço | Monitor sob o cursor |
| Seleção | A | Todas as telas |
| Seleção | C | Copiar a cor sob o cursor |
| Seleção | Esc ou botão direito | Cancelar |
| Editor | V A R E L P H T N B C | Mover, Seta, Retângulo, Elipse, Linha, Caneta, Marca-texto, Texto, Passo, Ocultar, Recortar |
| Editor | Shift ao desenhar | Quadrado/círculo, linhas a 45° |
| Editor | Ctrl+Z / Ctrl+Y | Desfazer / refazer |
| Editor | Ctrl+C / Ctrl+S / Ctrl+Shift+S | Copiar / salvar / salvar como |
| Editor | Ctrl+roda, Ctrl+0, Ctrl+1 | Zoom, 100%, ajustar à janela |

Se o Print abrir a Ferramenta de Captura do Windows, desative em Configurações › Acessibilidade › Teclado › "Usar a tecla Print Screen para abrir a captura de tela" (ou use Ctrl+Alt+R).

## Compilar e rodar

O SDK foi instalado de forma portátil em `%LOCALAPPDATA%\dotnet-sdk` (sem admin):

```powershell
$dn = "$env:LOCALAPPDATA\dotnet-sdk\dotnet.exe"
& $dn build Capturo.sln
& $dn test Capturo.sln
& .\src\Capturo\bin\Debug\net8.0-windows\Capturo.exe            # fica na bandeja
& .\src\Capturo\bin\Debug\net8.0-windows\Capturo.exe --capture  # já abre a seleção
```

Publicar um executável único: `& $dn publish src\Capturo -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true`.

## Estrutura

```
src/Capturo
  Capture/   ScreenGrabber (BitBlt da tela virtual), WindowFinder (EnumWindows + DWM),
             CaptureSession (estado da seleção em pixels físicos), OverlayWindow (uma por monitor)
  Editor/    Annotations (registros imutáveis), EditorHistory (desfazer por instantâneos),
             AnnotationRenderer (anotação → WPF), EditorWindow
  Services/  AppSettings (%APPDATA%\Capturo\settings.json), OutputService, HotkeyManager, TrayIcon, Log
tests/Capturo.Tests   geometria, histórico, anotações e nomes de arquivo
```

Configurações e log ficam em `%APPDATA%\Capturo`.

## Próximos passos

- Gravação de vídeo (Windows.Graphics.Capture + Media Foundation, cursor e cliques desenhados).
- OCR e QR Code com `Windows.Media.Ocr`.
- Captura com rolagem, tela de configurações e atalhos personalizáveis.
