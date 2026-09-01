# OffWorkCountdown（owc）

> 一顆待在系統匣裡的圓環，安靜地幫你倒數今天還要被榨多久。

沒有視窗、沒有安裝精靈、開機自動上工。它從 Windows 事件記錄檔偷看你今天幾點開機，
推算開工時間，然後在系統匣畫一圈進度環：藍色從正上方順時針把紅色一點一點蓋掉，
蓋滿就是下班——如果你還沒走，它就掉頭把藍色染回紅色，開始默默替你數超時。

中間的數字是精確資訊，圓環只負責給你「今天過了多少」的餘光焦慮。

- 🍱 午休「過了才扣」：中午 12:30 只扣 30 分鐘，不預支
- 🔋 用電池時預設不動畫，不跟你的續航過不去
- 🔔 剩最後 10 分鐘冒泡泡提醒你「準備下班嘍」
- 🪶 .NET 10 + AOT，單一 exe，零依賴

## 開始使用

### 懶人流（推薦）

把發佈出來的 `owc.exe` **直接丟進 `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`**，
登入時它就自己上工了。沒設定檔？全套預設值照樣跑（一天 8 小時、午休 12:00–13:00、最早 07:00 開工）。

### 想調參數流

在 `owc.exe` 旁邊放一份 `appsettings.json`，改成你的作息，再**透過 appsettings.json 客製化再建立捷徑**
指到 Startup 資料夾——這樣 exe 收在哪都行，設定跟著捷徑走。

```jsonc
{
  "WorkHours": 8.0,          // 一天工時（不含午休）
  "LunchStart": "12:00",
  "LunchEnd": "13:00",
  "EarliestStart": "07:00",  // 整夜開機也不會變成 00:00 開工
  "WarnMinutes": 10,         // 剩幾分鐘彈提醒
  "Strategy": "FirstEventOfDay", // 或 BootOrWake（不關機的人用這個比較準）
  "Animation": "Endgame",    // Off / Endgame / Always

  // 圓環配色，吃 #RRGGBB 或 #AARRGGBB
  "ColorDone": "#4C9AFF",         // 已完成的藍弧
  "ColorPending": "#E5484D",      // 未完成／超時染回的紅弧
  "ColorText": "#FFFFFF",         // 圖示文字與秒針點
  "ColorFill": "#222B33",         // 平時底色
  "ColorFillImminent": "#7A1B17"  // 警戒／超時底色
}
```

缺檔、打錯字、多逗號、色碼亂填都不會讓它崩——一律安靜退回預設值。

## 右鍵選單

在系統匣圖示上按右鍵可以：把開工時間往前／往後挪 15 分鐘、重新偵測、或直接結束。

## 建置

```powershell
dotnet publish
```

---

看到圓環轉整圈變全藍的那一刻，該收工了。🏃
