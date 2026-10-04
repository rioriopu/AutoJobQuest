# 第三者のソフトウェアの表示

AutoJobQuest 本体のライセンスは GNU Affero General Public License v3.0（[LICENSE](LICENSE)）。
このファイルには、AutoJobQuest のソースの一部の元になったものと、配布物（zip）に同梱しているライブラリの、
著作権表示とライセンスをまとめる。

## 1. ソースの一部の元になったもの

### Questionable

- 著作権者：the Puni.sh Quest Squad
- 入手先：https://github.com/PunishXIV/Questionable
- ライセンス：GNU Affero General Public License v3.0（全文は [LICENSE](LICENSE)）
- 元にした箇所：
  - `AutoJobQuest/Data/AetherytePlaces.cs` … 地図の印からエーテライトの位置を求める手順
  - `AutoJobQuest/Data/QuestDialogue.cs` … クエストの会話の表の行（QuestDialogueText）の形
  - `AutoJobQuest/Ipc/PluginInstaller.cs` … Dalamud の内部の InstallPluginAsync の探し方と引数の渡し方

### Automaton（ffxiv-bundleoftweaks）

- 入手先：https://github.com/Jaksuhn/ffxiv-bundleoftweaks
- 元にした箇所：`AutoJobQuest/Automation/MeldTask.cs` … マテリア装着の画面へイベントを送る手順

```
BSD 3-Clause License

Copyright (c) 2023, Puni.sh

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### ECommons

- 入手先：https://github.com/NightmareXIV/ECommons
- 元にした箇所：`AutoJobQuest/Automation/GameUi.cs` … 会話の窓を送る手順（AdvanceTalk）

```
MIT License

Copyright (c) 2023 NightmareXIV

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 2. 配布物（zip）に同梱しているライブラリ

### LuminaSupplemental.Excel 4.3.3（LuminaSupplemental.Excel.dll）

- 作者：Critical-Impact
- ソース：https://github.com/Critical-Impact/LuminaSupplemental
  （このパッケージの元のコミット：733ee95d4467e0722b454ede3e2e0aafac5d74a8）
- ライセンス：GNU General Public License v3.0（全文は [licenses/GPL-3.0.txt](licenses/GPL-3.0.txt)）
- 用途：モンスターの出現位置と、落とす品の表（ゲームのデータに無いため）

### CsvHelper 30.0.1（CsvHelper.dll）

- Copyright © 2009-2022 Josh Close
- ソース：https://github.com/JoshClose/CsvHelper
- ライセンス：MS-PL と Apache License 2.0 から Apache License 2.0 を選んで使う（全文は [licenses/Apache-2.0.txt](licenses/Apache-2.0.txt)）
- LuminaSupplemental.Excel が使う

### CSVFile 3.2.1（CSVFile.dll）

- Copyright 2006 - 2025 Ted Spence
- ソース：https://github.com/tspence/csharp-csv-reader
- ライセンス：Apache License 2.0（全文は [licenses/Apache-2.0.txt](licenses/Apache-2.0.txt)）
- LuminaSupplemental.Excel が使う

### Sylvan.Data.Csv 1.4.3（Sylvan.Data.Csv.dll）

- ソース：https://github.com/MarkPflug/Sylvan
- LuminaSupplemental.Excel が使う

```
MIT License

Copyright (c) 2025 Mark Pflug

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
