---
pageId: developers/build
contentRevision: 2
---

# Сборка проекта

Применимость: Rimlink 0.8.1 · RimWorld 1.7 · протокол 1.

## Как пользоваться {#usage}

Установи SDK из global.json; версии пакетов централизованы в Directory.Packages.props. Из корня выполни:

`dotnet build Rimlink.sln`

`dotnet test Rimlink.sln -nologo -v q`

Мод не включён в solution: закрой игру и используй PowerShell scripts/Build-ClientMod.ps1 -RimWorldDir &lt;game&gt; -InstallTo &lt;game/Mods&gt;. -BundleHarmony допустим лишь для локального запуска без отдельного Harmony, не релиза. scripts/Package-Release.ps1 собирает release ZIP.

Для wiki: npm --prefix wiki ci --ignore-scripts, затем run test:checks, check:content, check:reference, build, check:links. Node 24 закреплён отдельно. Для BridgeTools нужен отдельный Build-BridgeTools.ps1 и установленная игра.

## От чистого checkout до сборки мода {#build-track}

1. Прочти global.json и установи подходящий SDK; проверь dotnet --info. Не обновляй центральные зависимости ради обхода локальной ошибки.
2. Из корня выполни dotnet build Rimlink.sln, затем dotnet test Rimlink.sln -nologo -v q. Это собирает server/core/tools/tests, не игровой мод.
3. Для мода нужен установленный RimWorld 1.7 с Assembly-CSharp.dll и PowerShell. Закрой принадлежащую тебе тестовую игру, укажи -RimWorldDir и **новый -OutputDir** в Build-ClientMod.ps1. Без -InstallTo скрипт только собирает папку; не трогай чужой live Mods.
4. Проверь About, Languages и Assemblies результата. Harmony не включай в релиз; -BundleHarmony — только локальный вариант.
5. Установка в тестовую копию и запуск двух клиентов — следующий отдельный этап приёмки, не следствие успешной компиляции.

Скрипт очищает выбранный OutputDir, поэтому не направляй его на каталог исходников, игру или единственную копию данных.

## Ограничения и безопасность {#limits}

Не заменяй DLL работающей игры и не перезапускай чужой экземпляр. Успешный solution build не доказывает сборку мода и игру. Не коммить bin/obj/data/сейвы/секреты и generated wiki output.

## См. также {#related}

- [Тестирование и приёмка](./testing.md)
- [Вклад и сопровождение wiki](./contributing.md)
- [Размещение и запуск](../admins/hosting.md)
