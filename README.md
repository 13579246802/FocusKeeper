# FocusKeeper 专注计时器

一个键盘驱动的 Windows 专注计时器。为「坐得住、但容易飘」的人做的：开始之后你只需要记得三个键，剩下的它来兜底。

单文件、零依赖、本地运行，不上传任何数据。

| 主界面 | 标题与载体选择 | 全屏形态 | 统计 |
|---|---|---|---|
| ![](docs-ui.png) | ![](docs-ui-title.png) | ![](docs-ui-full.png) | ![](docs-ui-stats.png) |

## 它是怎么工作的

| 键 | 作用 |
|---|---|
| `1` | 开始（写一句现在要做什么；顺便选「这件事在不在电脑上做」） |
| `2` | 结束 |
| `3` | 走神 —— **计时不会停**，只记一笔，屏幕上给一句安慰 |
| `4` | 休息 |
| `5` | 统计 |
| `↑` `↓` | 开始前直接换等级：初级 15 分 / 中级 25 分 / 高级 45 分 |
| `←` `→` | 开始前微调目标分钟（等级按时长自动跟着变） |
| `Tab` | 开始前切换载体：电脑上小窗 / 离开电脑小窗 / 离开电脑全屏 |
| `F11` | 专注中随时切换小窗 / 全屏 |
| `Ctrl+E` | 导出 CSV |

## 设计取舍

**走神是零惩罚的。** 按 `3` 没有任何扣分、变红或"失败"提示。原因很直接：一旦走神有代价，人就不按了，数据当天就变成废纸。这个工具的价值全在数据真实上。

**核心指标不是总时长，是走神间隔中位数。** 总时长只证明你坐了多久；相邻两次走神之间的间隔从 4 分钟变成 7 分钟，才是真的进步。统计页里那个数字值得长期盯。

**计时器假设你会忘事。** 到点没按结束会提醒，再等几分钟没动静就按目标时长自动收工（时长截到目标，宽限期不算专注）；6 分钟没动键鼠按"离开"结束；锁屏、合盖同理。反过来说：你在离开电脑的状态下（看书、写字）计时，它不会因为你没碰键盘就判定你消失了。

**不做连续打卡。** 断一天就清零的机制，惩罚的恰恰是"回归"，只会让人破罐破摔。看本周总量就够了。

## 下载

到 [Releases](../../releases) 页面下载 `专注计时器-便携版.zip`，解压后双击 `FocusKeeper.exe`。

- 自带运行时，目标机器不需要安装任何东西（64 位 Windows）
- 数据存在 exe 旁边的 `data\` 文件夹里，纯文本，拷走文件夹就是搬家

## 构建

需要 .NET 9 SDK：

```bash
dotnet build
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o app
```

回归测试（36 项，覆盖状态机、存储修复、截断一致性、文案规范）：

```bash
app/FocusKeeper.exe --selftest
```

## 反馈

这个工具首先是为我自己做的，我每天都在用，所以它不会烂尾。

它肯定有 bug，也肯定有我做错的设计判断——发现了请直接提 issue，不用组织语言，也不用客气。能站住脚的意见会变成下个版本，bug 一般当天修。

比起 star，我更想要一个正确的产品。直接说哪里不对就行。

---

# FocusKeeper (English)

A keyboard-driven focus timer for Windows, built for people who can sit down but drift. Once you start, you only need three keys; the app handles the rest.

Single file, zero dependencies, fully local — nothing leaves your machine.

| Main | Title & mode | Fullscreen | Stats |
|---|---|---|---|
| ![](docs-ui.png) | ![](docs-ui-title.png) | ![](docs-ui-full.png) | ![](docs-ui-stats.png) |

## How it works

| Key | Action |
|---|---|
| `1` | Start (type what you're doing; pick whether the task lives on this PC) |
| `2` | Stop |
| `3` | Distract — **the timer keeps running**, it just logs the lapse with a line of comfort |
| `4` | Break |
| `5` | Stats |
| `↑` `↓` | Switch level directly before starting: Beginner 15 / Intermediate 25 / Advanced 45 min |
| `←` `→` | Fine-tune goal minutes before starting |
| `Tab` | Cycle mode before starting: on-PC window / off-PC window / off-PC fullscreen |
| `F11` | Toggle window / fullscreen any time while focusing |
| `Ctrl+E` | Export CSV |

## Design choices

**Distraction carries no penalty.** Pressing `3` never deducts anything, never turns red, never counts as "failure". The reason is blunt: the moment distraction has a cost, people stop reporting it, and the data becomes fiction that same day. This tool is worth exactly as much as its data is honest.

**The core metric is median distraction interval, not total time.** Total minutes only prove you sat there. When the median gap between lapses grows from 4 to 7 minutes, that is actual progress. That's the number worth watching on the stats page.

**The timer assumes you forget things.** Miss the stop key and it reminds you; stay silent a few minutes longer and it auto-closes at the goal duration (grace time is truncated, never counted as focus). Six minutes of no input ends the segment as "away"; so do lock screen and sleep. Conversely, when you work away from the PC (reading, writing), it won't declare you gone just because you stopped typing.

**No streaks.** A counter that resets after one missed day punishes exactly the people who come back. Weekly totals are enough.

## Download

Grab `专注计时器-便携版.zip` from the [Releases](../../releases) page, unzip, run `FocusKeeper.exe`.

- Self-contained — the target machine needs nothing installed (64-bit Windows)
- Data lives in `data\` next to the exe, plain text; moving the folder moves your history

## Build

Requires the .NET 9 SDK:

```bash
dotnet build
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o app
```

Regression tests (36 checks covering the state machine, storage repair, truncation consistency, and phrase rules):

```bash
app/FocusKeeper.exe --selftest
```

## Feedback

I built this for myself first, and I use it every day — so it won't be abandoned.

It certainly has bugs, and certainly some design calls I got wrong. If you find one, open an issue directly: no need to phrase it carefully, no need to be polite. Reasonable feedback becomes the next version, and bugs are usually fixed the same day.

I care more about a correct product than about stars. Just tell me what's wrong.
