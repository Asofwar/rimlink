# Ручной чек-лист (то, что автотесты не покрывают)

Отмечать при каждом релизе. Пункты добавляются задачами плана.

## Сервер

- [ ] **Steam-вход с настоящим билетом.** Нужен Web API key (https://steamcommunity.com/dev/apikey) в `Auth:SteamWebApiKey`.
      Клиент получает билет через `SteamUser.GetAuthSessionTicket` и вызывает `Auth.Login` — ожидается токен сессии.
      Если Steam отвечает `Invalid ticket` для ключа обычного (не издательского) аккаунта — записать это в `docs/server-setup.md`
      как требование и держать `Auth:AllowPassword=true` как запасной путь.
