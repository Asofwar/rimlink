---
pageId: admins/security-auth
contentRevision: 1
---

# TLS, вход и права

Применимость: Rimlink 0.8.1 · RimWorld 1.7 · протокол 1.

## Как пользоваться {#usage}

TLS по умолчанию включён. Укажи PEM CertificatePath/KeyPath либо используй самоподписанный сертификат; его pin сообщай игрокам независимым каналом. SelfSignedPath задаёт путь самоподписанного сертификата. Планируй ротацию заранее.

Steam-вход требует SteamWebApiKey и соответствующих Steam id для административных аккаунтов. Парольный вход включается AllowPassword; AdminLogins назначает первоначальных администраторов. Общий ServerPassword не заменяет личную аутентификацию.

Для интеграций используй отдельные Admin:Tokens с Name, Token, Scopes. Имя уникально, [a-z0-9-], до 32 символов; токен уникален, не короче 24. read читает, moderate включает read, chat разрешает внешнее сообщение, admin даёт все права. Минимизируй scope.

## Ограничения и безопасность {#limits}

Панель использует legacy Admin:ApiToken, не именованные токены. Cookie HttpOnly/Secure/SameSite=Strict, сессии в памяти сбрасываются рестартом; 5 неудач блокируют IP на 10 минут. Cookie-мутации требуют CSRF. Не используй --insecure вне изолированного теста. X-Rimlink-Actor — аудитная подпись 1–64 символа без control, не доказательство личности.

## См. также {#related}

- [Справочник REST API](./api.md)
- [Панель и модерация](./administration.md)
- [Discord-бот](./discord.md)
