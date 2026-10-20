# 發佈與 macOS 簽章

`.github/workflows/release.yml` 會先在 Ubuntu 跑一次單元測試，再平行 build 六個平台：

| 平台 | 產物 |
|---|---|
| win-x64、win-arm64 | `OpenUtau-Chiaki-<rid>-Setup.exe`、`-Portable.zip`、Velopack 更新檔 |
| osx-arm64、osx-x64 | `OpenUtau-Chiaki-<rid>.dmg`（有設定 secrets 時會簽章並公證） |
| linux-x64、linux-arm64 | `.AppImage`、`.zip`、`.tar.gz` |

## 觸發方式

- **只 build 不發佈**：GitHub → Actions → Release → Run workflow，填版本，`release` 不勾。產物在該次 run 的 Artifacts，保留 3 天。
- **正式發佈**：推一個 `v` 加數字開頭的 tag（例如 `v0.1.0`；`voicebank-*` 這類 tag 不會觸發），版本取自 tag：

  ```sh
  git tag v0.1.0
  git push origin v0.1.0
  ```

  也可以在 Run workflow 勾選 `release`（`beta`、`draft` 也從那裡選）。

## macOS 簽章與公證

沒有設定 secrets 時，workflow 照樣 build，只是 DMG 為 ad hoc 簽章，使用者要用 `xattr -rc` 才能開。設好以下五個 secrets 後，CI 會用 Developer ID 簽章、送 Apple 公證、把公證票據 staple 到 DMG 上。

| Secret | 內容 |
|---|---|
| `MACOS_CERTIFICATE_P12` | Developer ID Application 憑證（含私鑰）匯出的 .p12，經 base64 編碼 |
| `MACOS_CERTIFICATE_PASSWORD` | 匯出 .p12 時設定的密碼 |
| `APPLE_ID` | 開發者帳號的 Apple ID（email） |
| `APPLE_TEAM_ID` | 10 碼 Team ID |
| `APPLE_APP_PASSWORD` | Apple ID 的 App 專用密碼（不是登入密碼） |

### 1. 確認或建立 Developer ID Application 憑證

```sh
security find-identity -v -p codesigning
```

看到 `Developer ID Application: <名字> (<TEAM_ID>)` 就代表已經有了，括號內就是 Team ID。

沒有的話：Xcode → Settings → Accounts → 選你的 Apple ID → Manage Certificates → 左下「+」→ Developer ID Application。只有帳號的 Account Holder 能建立這種憑證。

### 2. 匯出 .p12

1. 打開「鑰匙圈存取」，左側選「登入」鑰匙圈、上方選「我的憑證」。
2. 找到 `Developer ID Application: …`，展開箭頭確認底下有私鑰。
3. 在憑證上按右鍵 →「輸出…」→ 格式選「個人資訊交換 (.p12)」→ 存成 `DeveloperID.p12`，設定一組密碼。

### 3. 產生 App 專用密碼

到 <https://account.apple.com> → 登入與安全性 → App 專用密碼 → 產生，名稱例如 `openutau-notary`。記下產生的 `xxxx-xxxx-xxxx-xxxx`。

### 4. 寫入 GitHub secrets

在本機執行（每個指令會提示輸入值，不會留在 shell 歷史）：

```sh
base64 -i DeveloperID.p12 | gh secret set MACOS_CERTIFICATE_P12 --repo chiakich/OpenUtau-chiaki
gh secret set MACOS_CERTIFICATE_PASSWORD --repo chiakich/OpenUtau-chiaki
gh secret set APPLE_ID --repo chiakich/OpenUtau-chiaki
gh secret set APPLE_TEAM_ID --repo chiakich/OpenUtau-chiaki
gh secret set APPLE_APP_PASSWORD --repo chiakich/OpenUtau-chiaki
```

設完後刪掉 `DeveloperID.p12`。也可以在 GitHub 網頁 Settings → Secrets and variables → Actions 手動新增。

### 5. 執行並驗證

跑一次 Release workflow（不勾 `release` 即可），在 log 確認 `Import signing certificate` 與 `Notarize DMG` 都成功。下載 DMG 後：

```sh
spctl -a -vvv -t open --context context:primary-signature OpenUtau-Chiaki-osx-arm64.dmg
xcrun stapler validate OpenUtau-Chiaki-osx-arm64.dmg
hdiutil attach OpenUtau-Chiaki-osx-arm64.dmg
spctl -a -vvv /Volumes/OpenUtau*/OpenUtau-Chiaki.app
```

`spctl` 應顯示 `accepted` 與 `source=Notarized Developer ID`。

### 在本機簽章（選用）

```sh
dotnet msbuild OpenUtau -t:BundleApp -p:Configuration=Release -p:RuntimeIdentifier=osx-arm64 \
  -p:UseAppHost=true -p:SelfContained=true -p:OutputPath=../bin/osx-arm64/
MACOS_SIGN_IDENTITY="Developer ID Application: <名字> (<TEAM_ID>)" bash scripts/create-macos-dmg.sh osx-arm64

# 公證憑證存進鑰匙圈一次，之後用 profile 名稱即可
xcrun notarytool store-credentials chiaki-notary --apple-id <APPLE_ID> --team-id <TEAM_ID>
NOTARY_PROFILE=chiaki-notary bash scripts/macos/notarize.sh OpenUtau-Chiaki-osx-arm64.dmg
```

## 注意事項

- 私有 repo 的 Actions 分鐘數有倍率（macOS 約 10 倍、Windows 2 倍），兩個 mac job 是主要花費；只在需要時手動觸發。
- 每個產物都含約 470 MB 的聲庫，artifacts 只保留 3 天以免超出儲存額度；Release 附件不計入額度。
- Windows 安裝檔沒有程式碼簽章，第一次執行會出現 SmartScreen 警告。
