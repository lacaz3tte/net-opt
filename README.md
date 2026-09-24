# Network Optimizer

Утилита для Windows 10/11 x64, которая **сама** ищет рабочую локальную сетевую конфигурацию для доступа к YouTube и Discord.

```text
Запустить NetworkOptimizer.exe
        ↓
Нажать [ AUTO DISCOVER & FIX ]
        ↓
Программа сама: snapshot → discover → apply → test → rollback → next
        ↓
✓ WORKING CONFIGURATION FOUND
```

Пользователь **не** переключает конфигурации вручную между тестами.

## Как запустить

1. Скачайте `NetworkOptimizer.exe` (папка `dist/`).
2. Запустите файл. Устанавливать Python, Node.js, .NET Runtime или WSL **не нужно**.
3. Нажмите **AUTO DISCOVER & FIX**.
4. Дождитесь завершения поиска.
5. Готово: при успехе конфигурация остаётся активной.

CLI (то же самое без окна):

```text
NetworkOptimizer.exe auto
```

## Что программа меняет

Только настройки **этой** Windows-машины, с обязательным snapshot и rollback:

| Настройка | Когда меняется |
|---|---|
| Прокси WinINET (Internet Settings, HKCU) | Существующий HTTP/SOCKS proxy, локальные инструменты, встроенный локальный TLS-split proxy |
| DNS активного адаптера | Только стратегия DNS и только если в `config/appsettings.json` включено `allowDnsChanges` **и** есть права администратора |
| Metric интерфейса VPN/TUN | Только если VPN/TUN уже есть и разрешены routing-изменения + администратор |
| Ничего | Direct, IPv4/IPv6 (проверка семейства адресов без смены системных настроек) |

Программа **не** устанавливает VPN, proxy, драйверы и чужое ПО. Если инструмент не найден — стратегия получает статус `UNAVAILABLE`, поиск продолжается.

Программа **не** занимается взломом чужих систем, MITM чужого трафика, DDoS или обходом чужой аутентификации.

## Rollback

Перед каждым изменением создаётся `NetworkSnapshot`.

- Неуспешный кандидат → rollback → следующий кандидат.
- **STOP** → безопасная остановка, rollback, исходное состояние.
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
NetworkOptimizer.exe scan       Обнаружение интерфейсов, proxy, DNS, VPN/TUN, локальных инструментов
NetworkOptimizer.exe test       Проверка YouTube и Discord прямо сейчас
NetworkOptimizer.exe status     Статус, saved configuration, interrupted snapshot
NetworkOptimizer.exe auto       Полный автоматический поиск
NetworkOptimizer.exe auto --best
NetworkOptimizer.exe rollback
NetworkOptimizer.exe monitor [--interval 60] [--auto]
```

Режимы поиска: **Fast** (первый стабильный успех, по умолчанию) и **Best** (сравнить несколько успешных кандидатов).

## Сборка из исходников

Требуется .NET 8 SDK (только у разработчика, не у пользователя).

```powershell
dotnet test
.\scripts\publish.ps1
```

Результат:

```text
dist/NetworkOptimizer.exe
```

Это self-contained single-file `win-x64`. Native AOT не используется: WPF с ним несовместим.

## Стратегии

- Direct
- IPv4 / IPv6
- Existing HTTP/HTTPS proxy (после handshake)
- Existing SOCKS4/SOCKS5 (после handshake)
- Windows Proxy (WinINET / WinHTTP / HTTP_PROXY)
- DNS (публичные резолверы, только с разрешением и обычно с UAC)
- Existing VPN/TUN
- Existing local tools (Clash, v2ray, Xray, sing-box и т.п. — только если уже запущены)
- Local TLS split proxy (локальный CONNECT-прокси на 127.0.0.1 для трафика этой машины)

Добавление новой стратегии не требует переписывать optimizer: реализуется `INetworkStrategy`.
