# Ручной чек-лист (то, что автотесты не покрывают)

Отмечать при каждом релизе. Пункты добавляются задачами плана.

## Сервер

- [ ] **Импорт модпака.** `rimlink-tools modpack import --from <Config>/ModsConfig.xml --mods-dir <RimWorld>/Data <RimWorld>/Mods <workshop>/294100 --server https://host:25560 --token <Admin:ApiToken> --insecure`
      (сначала с `--dry-run`): в списке есть все активные моды с версиями, у Workshop-модов заполнен `workshop_id`.

- [ ] **Лимит неавторизованных соединений.** Открыть 6 раз `openssl s_client -connect 127.0.0.1:25560 -alpn h2` без данных:
      шестое соединение закрывается сразу, первые пять закрываются через 30 с (`login timeout`).

- [ ] **Steam-вход с настоящим билетом.** Нужен Web API key (https://steamcommunity.com/dev/apikey) в `Auth:SteamWebApiKey`.
      Клиент получает билет через `SteamUser.GetAuthSessionTicket` и вызывает `Auth.Login` — ожидается токен сессии.
      Если Steam отвечает `Invalid ticket` для ключа обычного (не издательского) аккаунта — записать это в `docs/server-setup.md`
      как требование и держать `Auth:AllowPassword=true` как запасной путь.
