---
pageId: admins/security-auth
contentRevision: 18
---

# TLS, вход и права

Применимость: Rimlink 0.10.4 · RimWorld 1.7 · протокол 1.

## Как пользоваться {#usage}

TLS по умолчанию включён. Укажи PEM CertificatePath/KeyPath либо используй самоподписанный сертификат; его pin сообщай игрокам независимым каналом. SelfSignedPath задаёт путь самоподписанного сертификата. Планируй ротацию заранее.

Steam-вход требует SteamWebApiKey и соответствующих Steam id для административных аккаунтов. Парольный вход включается AllowPassword; AdminLogins назначает первоначальных администраторов. Общий ServerPassword не заменяет личную аутентификацию.

Для интеграций используй отдельные Admin:Tokens с Name, Token, Scopes. Имя уникально, [a-z0-9-], до 32 символов; токен уникален, не короче 24. read читает, moderate включает read, chat разрешает внешнее сообщение, admin даёт все права. Минимизируй scope.

Соединению даётся 30 секунд на аутентификацию. Успешная проверка токена и scope REST API либо cookie-сессии панели с требуемой CSRF-проверкой отмечает соединение авторизованным до выполнения операции. Это позволяет длительной административной операции завершиться. Права и учётные данные проверяются при каждом запросе.

## Разделение и отзыв доступа интеграций {#rotate-access}

Выдай мониторингу read, чат-мосту read+chat, модерации moderate по необходимости. moderate включает read, но не chat; admin даёт всё. Для каждого процесса создай отдельное имя/секрет. При отзыве одного не нужно менять все остальные. Изменения конфигурации применяй контролируемым перезапуском и проверкой отказа отозванного токена.

Панель использует только legacy Admin:ApiToken: named token не даёт вход в браузер даже со scope admin. Перезапуск завершает cookie-сессии. Для cookie-мутаций нужны X-Rimlink-Panel: 1 и допустимый Origin; Bearer-интеграции не должны эмулировать cookie.

Не передавай токены в URL. CLI --token может остаться в history/списке процессов: используй защищённое окружение оператора и не снимай полный командный журнал в публичный отчёт.

## Плановая замена сертификата {#rotate-cert}

До замены сохрани рабочую конфигурацию и объяви новый pin доверенным каналом. После перезапуска сравни фактически отданный сертификат с объявленным и проверь игровой клиент и бота. У клиента старый pin должен потребовать решения, а не молча обновиться.

Если игрок получил неожиданный новый pin, сначала проверь адрес/NAT/proxy и факт ротации. Не советуй массово включить --insecure или доверять любому сертификату. SelfSignedPath по умолчанию зависит от DataDir; случайный пустой mount может выглядеть как «ротация» нового сервера.

В разрабатываемом гильдейском бое выдача/обновление control grant — отдельная authenticated player мутация с server-derived стороной и текущей session/membership binding. ReconcileControl ограничен 2 RPC за 5 секунд на аккаунт (включая повторы); новая финансовая выдача дополнительно ограничена 5 секундами на сторону. Это не лимит боевых приказов: утверждённый hot transport 20 commands/s на сторону ещё подключается отдельно. Product capability пока закрыта.

Боевой канал WIP дополнительно закреплён за конкретным HTTP/2 connection ID, текущей session/membership, capability digest и server connection epoch. Повторное использование bearer/capability с другого соединения требует процедуры замены канала и CutApplied. В hot-path нет финансового commit: короткий read-only SQLite reader закрывается до освобождения S/B, cache не держит DB/file handles. Настройки по умолчанию остаются выключенными, пока администратор их не включит.

Новый executor Hello разделяет Execution и scoped RecoveryDelivery. Nonce одноразовый, TTL 30 секунд по monotonic clock; binding включает server boot и текущую credential generation. Cached reply не продлевает старую lease. HelloBegin ограничен 2/5s, HelloComplete — 4/5s. Runtime ID не является credential или аппаратной attestation: native facts проверяет доверенный shipping adapter. Старый профиль для cold Final не обходит отзыв текущего credential и не открывает выполнение нового боя.

## Ограничения и безопасность {#limits}

Панель использует legacy Admin:ApiToken, не именованные токены. Cookie HttpOnly/Secure/SameSite=Strict, сессии в памяти сбрасываются рестартом; 5 неудач блокируют IP на 10 минут. Cookie-мутации требуют CSRF. Не используй --insecure вне изолированного теста. X-Rimlink-Actor — аудитная подпись 1–64 символа без control, не доказательство личности.

## См. также {#related}

- [Справочник REST API](./api.md)
- [Панель и модерация](./administration.md)
- [Discord-бот](./discord.md)


В транспорте арбитра BattleExecutor использует отдельную схему authorization: RimlinkExecutor и свой секрет. Bearer сессии игрока/администратора не проходит этот вход, а executor credential не даёт доступ к Wallet или другим player RPC. Сервер сравнивает SHA-256 секрета в constant time, проверяет identifier/profile/lease и при включённом TLS enforcement отказывает plaintext-запросам. Heartbeat публикует текущую доступность без изменений asset authority; новая lease не наследует старую, cleanup hold блокирует новый Start. Реальная native квалификация и применение результата ещё не завершены.

Execution и RecoveryDelivery используют отдельный authenticated Hello challenge с runtime/profile/build/inventory/world/boundary и server boot binding. Initial Entry proof запрещает Game, player session, battle/tick context и pending callbacks. Новый runtime-курьер получает только delivery scope, а не право повторить симуляцию или подтвердить Entry исходного процесса. Tagged cleanup требует свежего challenge и durable отклонения запоздалой работы назначения.

Узкий StartReplayOnly допуск предназначен для исходного живого runtime при потере Start authorization: прежний UUID и exact initial обязательны. Он не разрешает новые Prepare/proposal/commands. RuntimeStarted proof записывается в существующий transport journal и не создаёт второй финансовый Start. Native/restore qualification этого потока ещё не завершена.

Вход в guild acceptance требует явных mode/stand ID с обеих сторон. Core и GABS `rimlink/connect` принимают отдельный выбор `acceptanceStandId` плюс `acceptanceCertificateSha256`, только для HTTPS loopback IP. Точный сертификат проверяется независимо от обычного pin store, без автоматического доверия новому pin. Default client такой stand автоматически не принимает. Арбитр использует `AcceptanceStandId` в собственной явной конфигурации; player session в его процессе запрещена. Внешний marker, чужой source или отсутствие защищённого provenance не дают допуска. После аутентификации Final возвращает повторяемый `Unavailable`, пока сервер ждёт подтверждения выданной команды или отзыва управления. Арбитр продолжает отправлять подтверждения по своему аутентифицированному каналу.
