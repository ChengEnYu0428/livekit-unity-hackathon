# AI 協作功能使用說明

此版在 `codex/yating-agent` 的雅婷辨識及 Unity 介面上加入 AI 協作。
影音、語音辨識與 MP3 流程仍由既有 LiveKit 程式處理。

## 功能與圖的對應

| 圖中的功能 | 實作 |
| --- | --- |
| 逐字稿服務 | 雅婷回傳的完整句子送入 AI 對話情境 |
| 歷史對話 | 保留同一次記錄中的早先逐字稿，以及最近 10 次 AI 問答 |
| 技術助理 | 使用目前問題、逐字稿及文件內容產生技術回答和下一步 |
| 技術參考資料 | 查詢指定資料夾內的 Markdown、UTF-8 TXT、文字型 PDF |
| 摘要與待辦整理 | 問題摘要、已回報的操作、目前狀態、待辦事項、下一步 |
| 回傳 Unity | LiveKit reliable data，分段傳送與 SHA-256 完整性檢查 |

語言模型服務可設定為 OpenAI 相容的 Chat Completions 端點，或本機 Ollama。
檢索採用中文雙字詞及英文單詞的 BM25 排序，再將相關文件交給模型，屬於檢索後生成。
目前不使用向量資料庫。最多索引 2,000 個段落，每次提供最相關的 4 段。
引用只允許模型從本次提供的文件中選擇，Unity 可查看文件名稱、頁碼與原文。

## 服務端設定

先安裝 `requirements.txt`，再設定 `.env` 或 Cloud Agent secrets。
AI 預設關閉，未設定時會顯示「AI 尚未設定」，不會以假回答代替模型結果。
實際 API 金鑰只放在 Agent 服務端，不能放到 Unity 或 Git。

OpenAI 相容服務：

```dotenv
AI_ENABLED=true
AI_PROVIDER=compatible
AI_BASE_URL=https://api.openai.com/v1
AI_MODEL=<你帳號可使用且支援 JSON 輸出的模型>
AI_API_KEY=<模型服務 API 金鑰>
```

使用本機 Ollama：

```dotenv
AI_ENABLED=true
AI_PROVIDER=ollama
AI_BASE_URL=http://127.0.0.1:11434
AI_MODEL=<你已下載的模型名稱>
AI_API_KEY=
```

Ollama 和 Agent 要能互相連線。Cloud Agent 中的 `127.0.0.1` 指 Cloud 容器本身，不能連到你的電腦。
API 格式參考：https://developers.openai.com/api/reference/resources/chat

## 放入技術資料

將確認可供本次協作使用的文件放入 `TranscriptAgent/knowledge/`。
隨附 `video_meeting_guide.md` 是根據本專案程式整理的視訊會議操作與疑難排解說明。
要評估正式 SOP 問答，請加入實際文件。掃描 PDF 需先做文字辨識再匯入。
可使用 `AI_KNOWLEDGE_DIR` 指定其他目錄。空值使用預設 knowledge 目錄。
文件在首次 AI 請求時載入，更新後需重啟 Agent。Docker 會把預設 knowledge 目錄一起打包。

## Unity 操作

1. 使用 `StreamingSDKSimpleSample` 場景，兩端加入同一房間。
2. 由其中一人開始記錄，說明現場問題，等待逐字稿產生。
3. 同一位發起者打開右上方「遠距 AI 協作」。左側同時顯示主要影像、另一位參與者及最近 6 段逐字稿，右側顯示 AI 建議與摘要。
4. 輸入問題並按「詢問 AI」。不輸入問題則分析最近的對話。
5. 「產生摘要」整理目前保留的對話。停止記錄後，AI 設定已啟用時也會自動產生最後摘要。
6. 按「匯出結果」，保存完整 JSON（含引用文件）及易讀 TXT。

輸出位於 `Application.persistentDataPath/Collaboration`。可用「查看技術建議／查看協作摘要」切換各自最近一筆結果，匯出目前正在查看的結果。

### 讓成果截圖呈現遠距協作

- 「切換來源」選擇真正連接眼鏡的本機或遠端影像，再按「標記為眼鏡」。標記是使用者指定的來源角色，系統不會把任意相機自動認定為眼鏡。
- 主要畫面顯示來源身分與本機預覽／LiveKit 遠端接收狀態。小畫面顯示另一位參與者，沒有影像時保留等待提示。
- 影像直接使用目前通話已解碼的材質，不建立第二路訂閱，不更動音訊或 ASR 流程。完整比例顯示，避免裁掉現場操作細節。
- 可以在工作畫面切換麥克風及開始／停止記錄。逐字稿按最新在前顯示說話者與時間。
- 「儲存截圖」保存當下實際 Unity 畫面為 PNG，與匯出結果放在同一個資料夾。截圖包含現場畫面、協作對話及目前 AI 結果，背景的連線設定欄位會被工作畫面覆蓋。
- 來源離開後會清空其畫面，不會自動換成別人的影像沿用眼鏡標記。可手動切換來源。斷線時會清除影像、AI 結果及來源標記。
- 建議以橫向畫面保存論文成果。直向畫面將影像與 AI 區域上下排列。
- 目前 AI 仍分析文字對話與技術文件，影像是供協作人員觀看，尚未加入 AI 影像辨識。
AI 視窗支援捲動長篇結果與顯示等待、錯誤、接收不完整、逾時等狀態。
中文使用作業系統字型；Android 發行版應確認裝置支援中文字型。
獨立的 Advanced API 測試場景不會自動加入此面板，可使用新增 SDK 方法自行介接。

## 記錄範圍與限制

- AI 請求只接受本次記錄的發起者，結果定向回傳給該使用者。
- 每個房間工作各自維護資料，新記錄會清除舊記錄的 AI 情境與問答。
- 不會讀取其他房間、其他公司或前次程式執行的對話。不提供跨會議永久記憶。
- 保留最近 500 段逐字稿，每次模型輸入最多約 22,000 個文字字元。
  超過範圍時，結果會標示未涵蓋全部逐字稿，不能當作完整長會議摘要。
- 同時只執行一個 AI 請求，重複 request_id 不會再次呼叫模型。
- 摘要與建議來自對話回報，不會自動操作設備，也不能證明故障已排除。
- 可靠傳輸不等於斷線後保證送達。Unity 若收到不完整結果，需重新提出請求。
- AI 結果與 MP3 完成事件獨立。收到錄音完成不表示 AI 摘要也已完成。

## 給教授看的實際測試

使用同一次「遠端看不到影像」的測試，依序保存：

1. 現場影像與逐字稿中的問題。
2. AI 面板中的問題、技術回答、引用文件與下一步。
3. 現場人員回報檢查結果後產生的摘要與待辦事項。

使用真實模型與真實對話後才稱為系統成果。單元測試以假模型驗證資料流程，不代表模型回答品質。

## 協定

控制 topic：`jorjin.collaboration.control.v1`

```json
{"action":"ask","session_id":"目前的記錄ID","request_id":"唯一請求ID","question":"遠端沒有影像該怎麼檢查？"}
```

`action` 可為 `ask` 或 `summary`。事件 topic 為 `jorjin.collaboration.event.v1`。
事件依序為 `busy`、一或多個 `result_chunk`、`result_complete`，失敗則為 `error`。
每段最多 8,000 原始 bytes（Base64 後約 10.7 KB），結果總長最多 64,000 bytes。
SDK 的 `OnLiveKitCollaborationPacket` 只轉交內部 Agent identity 前綴的事件。
該前綴是路由過濾，正式部署仍需由可信任的 Token 發行端限制 Agent 身分。

## 測試

```powershell
python -m unittest discover -s TranscriptAgent/tests -v
dotnet run --project Tests/CollaborationProtocol/CollaborationProtocol.csproj
```
