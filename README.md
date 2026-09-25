# Network Optimizer

Утилита для Windows 10/11 x64: **одна кнопка** запускает bundled zapret (`winws` + WinDivert) и сама перебирает профили desync, пока YouTube и Discord не заработают.

```text
Запустить NetworkOptimizer.exe  (UAC / от имени администратора)
        ↓
Нажать [ AUTO DISCOVER & FIX ]
        ↓
Программа сама: snapshot → winws profile → test YouTube/Discord → rollback → next
        ↓
✓ WORKING CONFIGURATION FOUND
```

Других способов (DNS, прокси, VPN, локальный TLS-split) больше нет. Пользователь **не** выбирает профиль вручную.

## Как запустить

1. Скачайте `NetworkOptimizer.exe` вместе с папкой `zapret/` (после `publish` они рядом).
2. Запустите файл. Устанавливать Python, Node.js, .NET Runtime или WSL **не нужно**. Windows покажет UAC: WinDivert требует администратора.
3. Нажмите **AUTO DISCOVER & FIX**.
4. Дождитесь завершения поиска. Fast-режим оставляет первый полный успех.
5. Готово: `winws` остаётся запущенным с найденным профилем.

CLI (то же самое без окна):

```text
NetworkOptimizer.exe auto
```

## Что программа меняет

Только DPI-обход **на этой** машине через официальный zapret `winws.exe` и драйвер WinDivert. Перед поиском делается snapshot; неуспешный профиль останавливает `winws` и пробует следующий.

Программа **не** ставит VPN, HTTP/SOCKS-прокси, не меняет DNS и не ходит в чужие системы. Перебираются только встроенные профили desync (fake/split/multisplit/ttl и т.п.) для хостов YouTube и Discord.

Бинарники zapret/WinDivert лежат в `third_party/zapret/` (MIT / LGPLv3, см. `NOTICE.txt`) и копируются в `zapret/` рядом с EXE.

## Rollback

Перед поиском создаётся `NetworkSnapshot`.

- Неуспешный профиль → stop winws → следующий профиль.
- **STOP** → безопасная остановка, winws выключается, исходное состояние.
- Аварийное завершение → при следующем запуске:

```text
Previous network configuration was not restored.
[ RESTORE ]   [ CONTINUE ]
```

Восстановить вручную:

```text
NetworkOptimizer.exe rollback
```

или кнопка **RESTORE ORIGINAL** после успеха.

## Где лежат данные

Не рядом с EXE, если каталог приложения недоступен для записи:

```text
%LOCALAPPDATA%\NetworkOptimizer\
    config\appsettings.json
    state\working.json
    state\pending-operation.json
    state\original-snapshot.json
    logs\network-optimizer-YYYYMMDD.log
```

В логах нет паролей, cookie, токенов и содержимого пользовательского трафика.

## Monitor

Следит за текущей конфигурацией и при падении YouTube/Discord запускает повторный поиск (`--auto`).

```text
NetworkOptimizer.exe monitor
NetworkOptimizer.exe monitor --interval 60 --auto
```

В GUI после успеха: кнопка **MONITOR**.

## Другие команды

```text
NetworkOptimizer.exe scan       Обнаружение интерфейсов и проверка, что zapret/winws доступен
NetworkOptimizer.exe test       Проверка YouTube и Discord прямо сейчас
NetworkOptimizer.exe status     Статус, saved configuration, interrupted snapshot
NetworkOptimizer.exe auto       Полный автоматический поиск профилей zapret
NetworkOptimizer.exe auto --best
NetworkOptimizer.exe rollback
NetworkOptimizer.exe monitor [--interval 60] [--auto]
```

Режимы поиска: **Fast** (первый стабильный успех, по умолчанию) и **Best** (сравнить несколько успешных профилей).

## Сборка из исходников

Требуется .NET 8 SDK (только у разработчика, не у пользователя).

```powershell
dotnet test
.\scripts\publish.ps1
```

Результат:

```text
dist/NetworkOptimizer.exe
dist/zapret/winws.exe
dist/zapret/WinDivert.dll
dist/zapret/WinDivert64.sys
dist/zapret/cygwin1.dll
dist/zapret/files/...
```

Это self-contained single-file `win-x64`. Папка `zapret/` обязана лежать рядом с EXE. Native AOT не используется: WPF с ним несовместим.

## Стратегия

Только **zapret / winws**. AUTO DISCOVER перебирает известные профили YouTube/Discord (fake+multisplit, split2, ttl, seqovl и другие) и оставляет первый, на котором оба сервиса отвечают по HTTP.
