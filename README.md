<img src="assets/qingting.png" width="86" alt="轻听图标">

# 轻听 · Qingting

Windows 桌面上的中英文语音输入工具。点好输入框，按住空格说话。松开后，轻听把整段转写放进剪贴板，并粘贴到原来的光标处。你可以修改文字，再按回车发送。

## 开始使用

从 [Releases](https://github.com/ToniAdreal/qingting/releases/latest) 下载 Windows x64 压缩包，解压后双击 `SpaceDictation.exe`。保持 `local_whisper.py` 与它同目录。首次使用时从悬浮窗进入设置，选择麦克风和识别方式。

**本地 Whisper** 支持离线中英混说。安装 python.org 的 Windows x64 Python 3.12 后，在 PowerShell 中运行下面两行。第一行安装识别引擎，第二行联网下载多语言 small 模型。日常听写在本机运行。

```powershell
py -3.12 -m pip install faster-whisper==1.2.1
py -3.12 -c "from faster_whisper.utils import download_model; download_model('small')"
```

**在线豆包** 需要火山引擎语音 API Key，并在同一项目开通「录音文件识别2.0」。此模式在松开后将录音和常用词发给火山引擎，费用依账号额度和用量计算。[服务控制台](https://console.volcengine.com/speech/new/setting/activate?projectName=default)

## 输入方式

- 短按空格正常打字。长按约 0.35 秒，看到「正在聆听」后开口。
- 自动粘贴默认开启。转写完成时，原输入框需要仍然有焦点。如果换了窗口、换了输入框，或应用没有提供可确认的编辑焦点，文字留在剪贴板，可手动按 Ctrl+V。
- 点击悬浮窗开始和结束录音时，结果只复制。全局空格和自动粘贴均可在设置中关闭。
- 蓝牙麦克风、常用词、重试等操作见[使用说明](使用说明.md)。

## 从源码构建

在装有 .NET Framework 4.8 的 Windows x64 电脑上，从项目目录运行 `./build.ps1`。脚本使用系统自带的 C# 编译器生成 `SpaceDictation.exe`。本地识别依赖按上面的步骤单独安装。

采用 [MIT 许可证](LICENSE)。模型权重与第三方依赖遵循各自的许可证。
