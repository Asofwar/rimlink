---
pageId: admins/discord
contentRevision: 1
---

# Discord-бот

Применимость: Rimlink 0.8.1 · RimWorld 1.7 · протокол 1.

## Как пользоваться {#usage}

Бот — отдельный процесс/контейнер с секцией RimlinkBot. Server.Url указывает HTTPS сервера, ApiToken — отдельный именованный серверный токен с нужными scope. StateFile сохраняет позицию feed, его каталог должен быть постоянным.

В Discord Developer Portal создай бота, включи необходимый Message Content intent для чат-моста, добавь его в выбранную гильдию с минимальными правами. Задай Discord.Token, GuildId, Language (en/ru), ModeratorRoleIds, AdminRoleIds и id каналов Server/Raids/Market/Moderation/Chat. Нулевые id из примера не являются готовой конфигурацией.

Для TLS настрой CertificateSha256 (pin) либо доверенную цепочку с CertificateName при необходимости. Проверь чтение статуса, ограничения ролей и публичный чат на тестовом канале, затем включай мост.

## Ограничения и безопасность {#limits}

Discord.Token и Server.ApiToken — разные секреты. Insecure=false по умолчанию; true только для изолированной сети. Серверные scopes и Discord-роли независимы. Мост пересылает публичный чат, не личные/союзные сообщения. Рестарт/gap требует перечитать состояние, а не выдумывать пропущенные события.

## См. также {#related}

- [TLS, вход и права](./security-auth.md)
- [Мониторинг и журнал событий](./monitoring.md)
- [Чат, сообщения и пинги](../players/chat-pings.md)
