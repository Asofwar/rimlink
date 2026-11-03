---
pageId: admins/hosting
contentRevision: 1
---

# Размещение и запуск

Применимость: Rimlink 0.8.1 · RimWorld 1.7 · протокол 1.

## Как пользоваться {#usage}

Сервер — ASP.NET Core 10 с gRPC/HTTP2, REST и SQLite. Для запуска исходников нужен SDK из global.json; для framework-dependent релиза — соответствующий runtime. Постоянно сохраняй весь Server:DataDir, а не только БД.

Контейнерный пример в репозитории публикует 25560:25560 и монтирует ./data в /data. Перед запуском заполни секреты окружения. Для своего build используй deploy/Dockerfile. Вне Docker запускай исполняемый файл релиза из стабильного рабочего каталога: относительные пути вычисляются от него.

Прокси обязан пропускать HTTP/2/gRPC и долгие потоки, а не только страницу панели. Проверь сохранение и live-поток с внешнего клиента. Останавливай штатно с предупреждением игроков.

### Минимальный запуск из исходников

Установи SDK из global.json, собери сервер и создай отдельный рабочий каталог данных. Следующие команды выполняются из корня репозитория; парольный вход включён только для примера. В рабочем сервере используй собственные длинные секреты из окружения, не фиксируй их в Git.

```powershell
# Windows / PowerShell
$env:Rimlink__Server__DataDir='./data'
$env:Rimlink__Auth__AllowPassword='true'
$env:Rimlink__Auth__AdminLogins__0='admin'
dotnet run --project src/Rimlink.Server -c Release
```

```sh
# Linux
Rimlink__Server__DataDir=./data Rimlink__Auth__AllowPassword=true Rimlink__Auth__AdminLogins__0=admin dotnet run --project src/Rimlink.Server -c Release
```

### Контейнер

Из корня репозитория настрой секреты окружения, затем выполни `docker compose -f deploy/docker-compose.yml up -d`. Проверь `docker compose -f deploy/docker-compose.yml logs --tail=100`. Пример использует latest; перед рабочим запуском закрепи проверенный тег/ digest. Не запускай вместе с bare-процессом на том же DataDir. Штатная остановка: `docker compose -f deploy/docker-compose.yml down`; не добавляй удаление томов.

### Минимальная конфигурация и проверка

Для framework-dependent пакета файл appsettings.json в каталоге сервера содержит секции верхнего уровня; внешняя оболочка Rimlink в JSON не нужна:

```json
{"Server":{"Port":25560,"DataDir":"./data"},"Auth":{"AllowPassword":true,"AdminLogins":["admin"]}}
```

Запускай пакет через `dotnet Rimlink.Server.dll` из стабильного каталога. TLS включён по умолчанию: для первого подключения сверяй fingerprint по доверенному каналу; для публичного сертификата укажи Tls:CertificatePath и Tls:KeyPath (PEM). Открой TCP 25560 на сервере и маршрутизаторе только для нужной сети. Ожидаемый результат: сервер слушает порт, /health отвечает, клиент успешно сохраняет и повторно загружает колонию. Панель без успешного игрового подключения ещё не доказывает работу HTTP/2 прокси.

## Ограничения и безопасность {#limits}

Один DataDir не должен обслуживаться несколькими серверами одновременно. Не копируй активную БД и сейвы произвольными независимыми копированиями. TLS обязателен по умолчанию; HTTP только для изолированной разработки.

## См. также {#related}

- [Справочник конфигурации](./configuration.md)
- [Мониторинг и журнал событий](./monitoring.md)
- [Резервные копии и восстановление](./backup-recovery.md)
