# 画面操作の E2E — カバレッジ表

対象: 配布物と同じ publish 済みの `Fuseo.exe`（既定は arm64）。操作はすべて本物の入力（SendInput のマウス・キー）と
UI オートメーション（System.Windows.Automation）で行い、結果は画面の状態・自動保存（`document.json`）・書き出したファイルの
中身（形式の許可リスト・目印の文字列が無いこと・ページ数・画素）で確かめる。

実行: リポジトリ直下で `./scripts/vm-win.sh e2e`（publish してから実行）／`./scripts/vm-win.sh e2e --no-publish`
（前回の exe を使う）／`--filter "FullyQualifiedName~ExportTests"`（一部だけ）。結果は Mac の `$FUSEO_E2E_OUT`
（既定 `${TMPDIR}/fuseo-e2e`）の `results/`（`e2e.trx`・`console.txt`・失敗時の `screenshots/`）。

テスト名は `クラス.メソッド`（`windows/tests/Fuseo.E2E/Tests/`）。

## メイン画面

| 操作 | テスト |
|---|---|
| 起動（空の画面・書き出し不可） | ImportTests.OpenButton_ImportsImage_AndShowsPage |
| ファイルを開く（ボタン） | ImportTests.OpenButton_ImportsImage_AndShowsPage ほか多数 |
| ファイルを開く（Ctrl+O）・開くダイアログのキャンセル・複数選択 | ImportTests.CtrlO_Cancel_DoesNothing_ThenMultiSelectImportsAll |
| ドラッグ＆ドロップ（別プロセスからの OLE ドロップ・画像と PDF） | ImportTests.DragAndDrop_FromAnotherProcess_ImportsFiles |
| ドロップした対応外のファイル（エラー表示） | ImportTests.DragAndDrop_UnsupportedFile_ShowsError_AndImportsNothing |
| ドロップしたパスワード付き PDF（パスワード入力 → 取り込み） | ImportTests.DragAndDrop_EncryptedPdf_AsksPassword_AndImports |
| 複数ページ PDF の取り込み | ImportTests.Pdf_MultiPage_ImportsEachPage |
| 取り込みエラー: 対応外の拡張子 | ImportTests.UnsupportedFile_ShowsError_AndImportsNothing |
| 取り込みエラー: 壊れた画像・壊れた PDF | ImportTests.BrokenFile_ShowsError_AndImportsNothing |
| 取り込みエラー: ページ数上限（1 ファイル 51 ページ・開いている分と合わせて 51 ページ） | ImportTests.PageLimit_PerFileAndTotal_ShowsError_AndImportsNothing |
| パスワード付き PDF: 間違い → 再入力 → 正しい（OK ボタン・Enter） | PasswordTests.WrongPassword_AsksAgain_ThenRightPasswordImports |
| パスワード付き PDF: キャンセル（エラーを出さない） | PasswordTests.Cancel_ImportsNothing_AndShowsNoError |
| 新しい書類（ボタン・Ctrl+N）・作業は一覧に残る | LibraryTests.NewDocument_ButtonAndCtrlN_KeepTheWorkInTheList |
| 道具の切替（ボタン 3 つ・V / R / B キー） | DrawingTests.ToolButtonsAndKeys_SwitchTools_AndKeysPaintWithTheChosenTool |
| 四角で塗る（座標・書き出しの画素） | DrawingTests.RectTool_PaintsExactlyTheDraggedArea |
| ブラシで塗る・太さ 3 種（細・中・太） | DrawingTests.Brush_ThreeSizes_PaintThinMediumThickLines |
| 選択（クリック・何も無い所で解除）・選択した四角の移動 | DrawingTests.SelectTool_DragMovesRect_AndUndoMovesItBack |
| 選択の削除（ボタン・Delete・Backspace） | DrawingTests.SelectTool_ClickSelects_DeleteByButtonDeleteKeyAndBackspace |
| 選択した自動検出候補の削除（OFF になって一覧に残る） | SidePaneTests.QrCandidate_ToggleOnOff_TypeDefault_AndSelectDelete |
| 元に戻す／やり直し（ボタン・Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z）・回転の取り消し | EditTests.UndoRedo_ButtonsAndAllShortcuts |
| 回転（左右のボタン・Ctrl+L / Ctrl+R）・マスクも一緒に回る | EditTests.Rotate_ButtonsAndShortcuts_RotateImageAndMasksTogether |
| 拡大・縮小・全体表示（ボタン）・Ctrl+ホイール・縮小中に塗った位置 | ViewTests.Zoom_ButtonsCtrlWheelAndFit_AndPaintingStillLandsOnTheImage |
| パン（右ドラッグ・中ドラッグ・Space+左ドラッグ・ホイール・Shift+ホイール）・パンで塗らない | ViewTests.Pan_RightMiddleSpaceDragAndWheel_MoveTheViewWithoutPainting |
| ページ一覧のクリックで選択・上へ・下へ（端で押せない）・削除・書き出しの順 | PageRailTests.ClickSelect_MoveUpDown_Remove_ChangesExportOrder |
| 書類の種類の変更（チェックリスト・注意バナーが変わる・元に戻せる） | SidePaneTests.DocumentType_ShowsBannerAndChecklist_BannerCloses_ExportStillWarns |
| 注意バナーを閉じる（書き出しシートには再掲） | SidePaneTests.DocumentType_ShowsBannerAndChecklist_BannerCloses_ExportStillWarns |
| チェックリストのチェック（付ける・外す・保存） | SidePaneTests.ChecklistCheckBox_IsSavedAndCleared |
| 自動検出候補（QR）のオンオフ・種類による既定値 | SidePaneTests.QrCandidate_ToggleOnOff_TypeDefault_AndSelectDelete |
| ウィンドウを閉じる（保留中の自動保存を確定してから終わる） | LifecycleTests.CloseRightAfterEditing_FlushesThePendingSave |
| 再起動後に自動保存から再開（一覧 → 開く → 同じ結果を書き出せる・書き出し済みの表示） | LifecycleTests.Restart_ResumesFromAutosave_AndExportsTheSameResult |

## 文字認識による自動検出（W9-3・受け入れ W9-5）

入力は実行時に WPF で描く合成カード（`Support/SynthCards.cs`・Yu Gothic UI か Meiryo・番号は見るからに合成の本体＋検査用数字。
ファイルには描いた画像だけを VM ローカルに書く）。日本語の文字認識が入っている Windows が前提。

| 操作 | テスト |
|---|---|
| 取り込み時の種類の自動判定・「自動」の表示・伏せた番号の候補・自動判定は元に戻す対象外 | AutoDetectTests.MyNumberBack_AutoType_Badge_MaskedNumber_RawNumberNowhereInUi |
| 生の番号が画面のどこにも出ない（メイン画面と書き出しシートの UIA の文字を全部集めて確認）・書き出しで番号が塗られる | AutoDetectTests.MyNumberBack_AutoType_Badge_MaskedNumber_RawNumberNowhereInUi |
| チェックリストの案内文（自動で探します・位置で候補にしています・切り抜くと位置で候補になります・自動で探します（見つからなければ位置で候補にします）） | AutoDetectTests.MyNumberBack_AutoType_Badge_MaskedNumber_RawNumberNowhereInUi、AutoDetectTests.CardOnDesk_ShowsCropHint_CropMakesFixedRegionCandidates |
| 逆さの写真の自動正立（目印の位置・書き出し・保存された向き・元に戻す対象外） | AutoDetectTests.UpsideDownCard_IsTurnedUprightAutomatically_NotAnUndoStep |
| 「文字を読み直す」（回してから読み直す → 候補の作り直し → 元に戻す 1 回で戻る・回転は別の 1 回） | AutoDetectTests.Reread_AfterRotating_RebuildsCandidates_OneUndoRestores |
| 切り抜きの案内（机の上の小さなカード）→ 切り抜くと案内が消えて固定領域（性別・臓器提供）の候補・元に戻す | AutoDetectTests.CardOnDesk_ShowsCropHint_CropMakesFixedRegionCandidates |

## フォルダ一括処理・一覧（W9-4・受け入れ W9-5）

| 操作 | テスト |
|---|---|
| フォルダのドロップ（新しい作業として開く・前の作業が一覧に残る・対応外の件数・サブフォルダは見ない・論理順） | FolderAndGridTests.FolderDrop_OpensAsNewWork_PreviousWorkStaysInList_SkipsUnsupported |
| 「フォルダを開く」（フォルダの選択ダイアログ＝OpenFolderDialog・キャンセル・選んで開く） | FolderAndGridTests.OpenFolderButton_CancelDoesNothing_ThenPicksFolder |
| 一覧表示（セル数・「ファイル名（p/n）」・マスクなしの赤字・マスク数・セルのクリックで 1 枚ずつに戻る） | FolderAndGridTests.GridView_CellsNoMaskMarks_ClickOpensSinglePage |
| 書き出しの「書類ごとに分ける」（保存先フォルダの選択・既存の X-masked.pdf の横で -2・既存の Y-masked_1.jpg の横で -2_1/-2_2・既存と元のファイルが 1 バイトも変わらない・マスクなし書類の列挙・完了表示と閉じる） | FolderAndGridTests.ExportSeparate_NextToExistingFiles_AddsSuffix_LeavesExistingAndOriginalsUntouched |

## 切り抜き

| 操作 | テスト |
|---|---|
| 切り抜きを開く・四隅のドラッグ・適用・書き出しが切り抜いた範囲だけ | CropTests.DragFourCorners_Apply_ExportsOnlyTheCroppedArea |
| 不正な四角形（適用できない・警告）・全体を使う・キャンセル | CropTests.InvalidQuad_DisablesApply_UseWholeRestores_CancelKeepsPage |
| 手で塗った所がある時の確認（キャンセル／OK）・元に戻す | CropTests.Crop_WithHandPaintedMask_AsksFirst_CancelKeeps_OkClears_UndoRestores |

## 書き出し

| 操作 | テスト |
|---|---|
| 書き出し（ボタン）→ PDF → プレビュー → 保存ダイアログ → 保存・状況表示・書き出し済みの保存 | ExportTests.Pdf_ViaButton_FromPdfWithText_RasterOnlyAndPainted |
| 書き出し（Ctrl+E）→ JPEG（EXIF・XMP の目印が消える） | ExportTests.Jpeg_ViaCtrlE_FromJpegWithExif_NoMetadataAndPainted |
| PNG・複数ページ（_1, _2）・上書きの確認（キャンセル／OK） | ExportTests.Png_MultiPage_WritesNumberedFiles_AndAsksBeforeOverwriting |
| マスク 0 件の警告・ボタンの文言 | ExportTests.ZeroMask_WarnsAndRenamesSaveButton_OnlyWhileAPageHasNoMask、SidePaneTests.QrCandidate_ToggleOnOff_TypeDefault_AndSelectDelete |
| 種類の注意書きの再掲 | SidePaneTests.DocumentType_ShowsBannerAndChecklist_BannerCloses_ExportStillWarns |
| シートのキャンセル・保存ダイアログのキャンセル（シートに戻る）・Esc で閉じる | ExportTests.CancelSheet_CancelSaveDialog_AndEscape_WriteNothing |

書類ごとに分けた書き出しの出力も同じ検査（形式・目印・画素）を通す（FolderAndGridTests）。

書いたファイルの検査（`Support/OutputCheck.cs`）: JPEG は SOI・APP0・DQT・SOF0・DHT・SOS・EOI だけ、PNG は IHDR・IDAT・IEND だけ、
PDF は `%PDF-1.4`・`/Producer (Fuseo for Windows)` があり `/Font`・`Tj`・`/Annots`・`/Metadata`・`/Title` 等が無い。入力に
入れた目印の文字列（EXIF・XMP・PNG の tEXt・PDF のテキストと文書情報）が出力のバイト列に無い。画素は寸法・塗った所が黒い・
塗っていない所が元の色・回転と切り抜きの反映を確かめる。

## 作業中の書類（ライブラリ）・設定・バージョン情報

| 操作 | テスト |
|---|---|
| 一覧の「開く」ボタン・ダブルクリック（マスクと回転が戻る） | LibraryTests.OpenButtonAndDoubleClick_RestoreMasksAndRotation |
| 1 件削除（確認のキャンセル／OK） | LibraryTests.DeleteOne_ConfirmCancelKeeps_OkDeletes |
| すべて削除（確認のキャンセル／OK） | LibraryTests.DeleteAll_ConfirmCancelKeeps_OkDeletesEverything |
| 設定: 保存場所の表示・すべて削除（開いている間は不可・件数・確認）・閉じる（ボタン・Esc） | SettingsAndAboutTests.Settings_ShowsIsolatedStorage_DeleteAllFollowsState_CloseButtonAndEscape |
| 設定: 保存場所を開く（エクスプローラー） | SettingsAndAboutTests.Settings_OpenFolder_OpensExplorerOnTheStorageFolder |
| 設定: 言語の変更・再起動後の反映（英語 → 日本語に戻す） | SettingsAndAboutTests.Settings_Language_English_AppliesAfterRestart_AndBack |
| バージョン情報: 版・ライセンス・コピー 2 つ・閉じる | SettingsAndAboutTests.About_ShowsVersion_CopiesBothAddresses_AndCloses |

## E2E で見つけて直した不具合

| テスト | 内容 |
|---|---|
| ImportTests.DragAndDrop_ErrorDialog_DoesNotBlockTheDragSource | 対応外・壊れたファイルをドロップすると、エラーのメッセージボックスを Drop の処理の中で出していたため、閉じるまでドラッグ元（エクスプローラー等）が固まった。2026-10-31 修正（取り込みを Drop が戻った後に始める） |

## 時間の上限（止まったまま残らないように）

- 各待ち: ポーリングで既定 15 秒（起動 60 秒・書き出しの準備 30 秒）。時間切れは何を待っていたかを書いて失敗。
- 1 テスト: 3 分（`E2ETest.TestTimeout`）。超えたら見張りが押したままの入力を離し、そのテストのアプリを強制終了して失敗させる。
- testhost: 1 テストが 6 分動かなければ `--blame-hang-timeout 6m` で落とす。
- 全体: `run-e2e.ps1 -TimeoutMinutes`（既定 30 分・`FUSEO_E2E_TIMEOUT_MIN`）。Mac 側の `vm-win.sh e2e` も +5 分で打ち切る。
  止めるのは E2E の testhost と `C:\fuseo-e2e\app\Fuseo.exe` から起動した Fuseo だけ。

## E2E で自動化していない操作と代わりの確認

| 操作 | 理由 | 代わりの確認 |
|---|---|---|
| 終了時の「保存しています…」表示（保存が 3 秒を超えたときだけ出る） | 保存を 3 秒以上遅らせる手段が製品に無い（遅いディスクを作れない）。保存の確定そのものは E2E で確認済み | 手動: 大きな PDF（50 ページ）を取り込んだ直後に × で閉じ、表示が出て保存後に閉じること |
| 自動保存の失敗表示（ステータスバー） | 保存先を書けなくすると隔離のフォルダの外の権限操作が要る | Fuseo.App.Tests: AutoSaveViewModelTests.SaveFailure_IsShownInStatusBar |
| 読めない作業（壊れた document.json）の一覧表示と削除 | 一覧の表示は単体で確認済み。E2E では壊れた作業を作る準備が製品の外の操作になる | Fuseo.App.Tests: FileWorkLibraryTests.BrokenWorks_AreListedAsUnreadable_AndCanBeDeleted・WorkWithOutsidePath_IsUnreadable_AndCannotBeLoaded／手動: データフォルダの document.json を壊して起動 |
| HEIC の取り込み・HEIF 拡張が無いときのエラー | VM に入っている拡張に依存する | Fuseo.Input.Tests: ImportPolicyTests.UndecodableHeic_ReportsHeicSpecificError／手動 |
| 予期しないエラーのダイアログ（DispatcherUnhandledException） | 製品に故意に例外を起こす入口が無い | 手動では確認できない（コードレビュー） |
| 開くダイアログの「ファイルの種類」での絞り込み | Windows 標準のダイアログの機能 | 手動 |
| 日本語の文字認識が入っていない Windows での案内（右ペイン上部） | VM は日本語版で文字認識が入っている（外すと他の E2E が前提を失う） | Fuseo.App.Tests: AnalysisWiringTests.WithoutRecognizer_BehavesAsBefore_AndShowsDismissableNotice |
| 顔写真の自動検出 | 合成の画像では顔検出が働かない（実写をテストに入れない） | Fuseo.Input.Tests の顔検出の空振り確認と、W9-2 の実物での確認（docs/windows-design.md §10.14） |
| 実物に近い書類での検出精度（斜めの写真・幅 800px・A4） | 画面操作で回すと時間がかかる | `tests/Acceptance` の cards モード（make_cards.py の合成コーパス 22 枚を本番の部品で解析） |
