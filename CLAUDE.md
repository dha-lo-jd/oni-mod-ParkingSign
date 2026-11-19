# CLAUDE.md

ONI mod（Parking Sign / Hanging Parking Sign。緑の信号で、チェックした複製人間を最優先の到達できる看板へ呼ぶ）。Build: `dotnet build ParkingSign.sln`（Debug は `Local.Build.props` の `ModInstallPath` へコピーする。確認だけなら `-p:ModInstallPath=`）。共通事項は HQ（親フォルダ）の `CLAUDE.md`。

## 構成（ParkingSign/）
- `ParkingSignController`: 看板 1 基ぶんの状態（選択された複製人間、信号、優先度、目標マス、ゴースト）。`Sim1000ms` で目標マスを更新し、選択中の複製人間の勝者看板を 1 秒に 1 回再確認する。
- `ParkingSignRegistry`: 全看板の登録、複製人間ごとの勝者看板の決定とキャッシュ、パッチが呼ぶ問い合わせ（経路の遮断、案内先マス）。優先度が同じなら先に建てた看板（`SpawnOrder`）が勝つ（セーブされず、ロードのたびに振り直される）。
- `ParkingSignPatches`: Harmony パッチ（`Grid.HasPermission`、`Navigator.CanReach`、`MoveToLocation`、`FindNextChore`、`ChoreDriver.SetChore` など）。`UserMod2.OnLoad` が `PatchAll` 済みなので、もう一度呼ばない。
- `ParkingSignLog.Trace` は DEBUG 専用（Release ではコンパイルで消える）。

## 落とし穴
- **勝者看板のマスに立っている間は、他の看板への経路探索結果が古い（遮断されたまま）。** `FindWinningReachableSign` の `probeBlocked` はそのための仮定で、その間は他の看板を「行ける」と見なす。定期再確認（`RecheckMinionWinner`）は、勝者のマスに立っている間は見送る。外すと、行けない古い看板へ戻る振動が起きる（実機テストで確認済み）。
- 床置きの看板は完成したタイルが土台として要る（自然岩では建たない）。ハーネスのテストは自前でタイルを敷く。
- 翻訳キーは `Localization.Initialize` の後置パッチ（`HarmonyPriority.Last`）で登録する。PLib の翻訳適用より後に走らせる必要がある。

## 配布物と Workshop
- `dotnet build -c Release` が `release/ParkingSign`（git 管理外）に配布用フォルダを作る。Release は `ModInstallPath` へコピーしない。
- Workshop の文章は `Workshop/description.{en,ja}.bbcode.txt` と `changelog.bbcode.txt`。先にこれらを直し、あとで Steam に反映する。版を上げたら更新履歴に書く。
- `minimumSupportedBuild` は現行ブランチでは `744825`（HQ の `CONVENTIONS.md` の `min-build`）。

## 実機検証
`harness.json` と `ParkingSign/HarnessTests.cs`（`#if DEBUG && HARNESS`。`Local.Build.props` に `HarnessDllPath` があるときだけ有効）。
実行: `python C:/Users/dha_1/ONI-Modding/GravitasTestHarness/Gravitas.TestProbe/probe.py run --project . --tests '{"tags":["parkingsign","conformance"]}' --summary`
ゲームを起動し本番の mods.json を書き換えるので、実行前にユーザーに確認する。

テストが見ているもの: 建物の登録、翻訳とパッチの二重適用（conformance）、勝者看板が到達不能になったときに次の看板へ代わること。
見ていないもの: 実際の信号配線での動作、ぶら下げ看板、優先度の変更、保存と読み込み、UI（サイドスクリーン）。
