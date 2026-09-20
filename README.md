# LiveKit Unity Meeting

這個 repository 包含 Unity／眼鏡視訊會議程式、LiveKit Transcript Agent，以及會議建立與 Token 發放服務。

## 專案結構

- `Assets/`：Unity 與眼鏡整合程式碼、場景及第三方 SDK。
- `Packages/`、`ProjectSettings/`：Unity 套件與專案設定。
- `TranscriptAgent/`：會議逐字稿與 MP3 錄音 Agent。
- `MeetingBootstrapServer/`：會議路由與 LiveKit Token API。

## 開啟 Unity 專案

1. 使用 Unity Hub 加入這個資料夾。
2. 使用 Unity `6000.2.10f1` 開啟。
3. 開啟 `Assets/Jorjin/StreamingSDK/Samples/Scene/StreamingSDKSimpleSample.unity`。

## LiveKit 設定

- Unity 測試可透過 Editor Token 設定工具填入同一個 LiveKit Project 的 URL、API Key 與 API Secret。
- 正式 APK／EXE 應只呼叫 `MeetingBootstrapServer`，不可把 API Secret 寫進 Unity 或打包檔。
- `TranscriptAgent/livekit.toml`、`.env`、錄音輸出及有效 Token 不會提交到 Git。
- 請依 `MeetingBootstrapServer/.env.example` 與 `TranscriptAgent/.env.example` 建立各環境自己的設定。

## 測試

```powershell
python -m pytest TranscriptAgent/tests
python -m pytest MeetingBootstrapServer/tests
```

大型原生 SDK 檔案使用 Git LFS；clone 後請確認已安裝 Git LFS。
