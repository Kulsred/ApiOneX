# ApiOneX

Автор: **kulsred dev :3**

> Сделано по фану за 10 минут с Кодексом.

Локальный DeepSeek Web API proxy на ASP.NET Core / .NET 8.

## Архитектура

`Endpoints → DeepSeekClient → DeepSeek Web API`

`DeepSeekClient → IPowSolver → NodePowSolver → оригинальный WASM`

`DeepSeek Web SSE → StreamParser → TextDelta → OpenAI SSE / JSON`

```text
Program.cs                      DI, HTTP transport, обработка ошибок
Models/Contracts.cs             OpenAI DTO, промежуточный TextDelta
Services/AuthSession.cs         deepseek-auth.json и CookieContainer
Services/DeepSeekClient.cs      создание сессии, challenge, completion
Services/StreamParser.cs        SSE framing и DeepSeek patch protocol
Services/NodePowSolver.cs       IPowSolver и отменяемый процесс Node.js
Endpoints/ChatEndpoints.cs      /health, /v1/models, /v1/chat/completions
Scripts/pow-bridge.cjs          stdin JSON → stdout answer
Scripts/pow.cjs                 оригинальный lib/pow.js (MIT)
Tests/                          автономные проверки без NuGet-пакетов
```

## Запуск

Клонирование:

```powershell
git clone https://github.com/Kulsred/ApiOneX.git
cd ApiOneX
```

Из каталога клонированного репозитория:

```powershell
Copy-Item deepseek-auth.example.json deepseek-auth.json
# Заполните deepseek-auth.json своей браузерной сессией.
dotnet build
dotnet run --no-build
```

Приложение по умолчанию слушает `http://127.0.0.1:3001`; Node-прокси может продолжать использовать свой порт.
Нужны .NET 8 SDK и Node.js >= 18 (проверено с Node 24). `global.json` выбирает установленный здесь SDK 8.0.400. На другой машине выберите свой установленный SDK 8 или 9; TargetFramework может остаться net8.0. NuGet.Config очищает внешние источники: проект использует только ASP.NET Core shared framework.

Auth-файл по умолчанию читается из `deepseek-auth.json` в корне приложения. Секреты не копируются в новый проект и не выводятся в логи. Для другого расположения:

```powershell
$env:DeepSeek__AuthFile = 'C:\path\to\deepseek-auth.json'
# Необязательно:
$env:DeepSeek__NodePath = 'C:\Program Files\nodejs\node.exe'
dotnet run --no-build
```

Формат auth:

```json
{
  "token": "YOUR_TOKEN",
  "cookies": "ds_session_id=...; smidV2=...",
  "wasmUrl": "https://fe-static.deepseek.com/chat/static/sha3_wasm_bg.7b9ca65ddd.wasm",
  "userAgent": "USER_AGENT_FROM_YOUR_BROWSER"
}
```

Поддерживаются также `cookie` из оригинального проекта, `cookies` как словарь name/value или массив браузерных объектов `{name,value,domain}`. Массив импортируется для единственного origin chat.deepseek.com с путем `/`; это не полное восстановление браузерного cookie store (expiry/path не сохраняются). `hif_dliq` и `hif_leim`, если заданы, идут в соответствующие `x-hif-*` заголовки. Auth читается при старте; после обновления файла перезапустите процесс.

```powershell
Invoke-RestMethod http://127.0.0.1:3001/health
Invoke-RestMethod http://127.0.0.1:3001/v1/models
$body = @{model='deepseek-chat'; messages=@(@{role='user';content='Привет!'});stream=$false} | ConvertTo-Json -Depth 8
Invoke-RestMethod http://127.0.0.1:3001/v1/chat/completions -Method Post -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($body))
```

SSE из PowerShell:

```powershell
'{"model":"deepseek-reasoner","messages":[{"role":"user","content":"Explain async/await"}],"stream":true}' | curl.exe -N http://127.0.0.1:3001/v1/chat/completions -H 'Content-Type: application/json' --data-binary '@-'
```

## DI и HTTP

Выделенный singleton HttpClient владеет SocketsHttpHandler и CookieContainer одной учетной записи. DNS обновляется через PooledConnectionLifetime. Нет пересоздания cookie jar при ротации handler. Авторизация и User-Agent задаются только из локального auth, а Cookie добавляет handler. Перенаправления отключены. Для нескольких аккаунтов нужны отдельные transport/session на каждый аккаунт; общий контейнер использовать нельзя.

Это осознанный вариант вместо AddHttpClient: Microsoft отдельно предупреждает о совместном использовании и потере cookies при пуле handlers IHttpClientFactory:
https://learn.microsoft.com/en-us/aspnet/core/fundamentals/http-requests?view=aspnetcore-8.0#cookies

Таймаут заголовков HTTP — 2 минуты; всего запроса, включая чтение SSE, — 5 минут. RequestAborted проходит через HttpClient, StreamReader и PoW; процесс Node при отмене уничтожается вместе с дочерними процессами. PoW дополнительно ограничен 45 секундами. После начала SSE ошибки возвращаются SSE-объектом `error`, без финального успешного `[DONE]`.

## PoW

Последовательность из текущего Node-проекта:

1. POST `/api/v0/chat/create_pow_challenge`, `{ "target_path": "/api/v0/chat/completion" }`.
2. Из `data.biz_data.challenge` прочитать algorithm, challenge, salt, expire_at, difficulty, signature.
3. Передать challenge в оригинальный `solvePOW`. Он загружает WASM, выделяет память через `__wbindgen_export_0`, формирует UTF-8 prefix `salt_expire_at_`, вызывает `wasm_solve`, читает status int32 и answer float64.
4. Сериализовать `{algorithm,challenge,salt,answer,signature,target_path}` в UTF-8 JSON и Base64.
5. Отправить результат в `x-ds-pow-response` запроса completion. На каждый новый запрос получать новый challenge.

Это не заглушка и не произвольный SHA-256: используется исходный WASM ABI. Мост проверяет algorithm `DeepSeekHashV1`. Если DeepSeek поменяет ABI/алгоритм, обновляется IPowSolver, а не endpoint. Срок действия challenge обрабатывает upstream; автоматических повторов с тем же proof нет.

Для полностью C# варианта можно реализовать IPowSolver через WASM runtime с импортом `wbg`, доступом к linear memory и теми же exports. Это потребует выбранного runtime, проверки ABI и тестов на известных challenge/answer. Переписывать DeepSeekHashV1 на SHA3 без проверки эквивалентности нельзя. Текущий мост запускает новый процесс на каждый запрос: он прост и изолирован, но WASM cache исходного модуля не сохраняется между запросами. Для высокой нагрузки нужен постоянный worker с очередью/correlation ID либо .NET WASM runtime.

## Стриминг и ограничения совместимости

StreamParser читает UTF-8 через StreamReader, собирает многострочные data frames, игнорирует SSE comments, применяет initial response, fragments APPEND, patch operations, continuation без p и отдельные THINK/REASONING фрагменты. В OpenAI потоке reasoning идет в `delta.reasoning_content`, ответ — в `delta.content`. Все чанки имеют стабильные id/created/model. При успешном завершении отправляются finish_reason=stop и `[DONE]`.

stream=false собирает те же TextDelta в `chat.completion`. Для обычного ответа finish_reason=stop, для вызовов инструментов — tool_calls. Точные данные usage недоступны: возвращается null. При stream_options.include_usage=true перед [DONE] отправляется дополнительный chunk с choices=[] и usage=null. Заголовок X-ApiOneX-Usage: unavailable явно сообщает об этом.

Модели — локальные псевдонимы режимов Web API (`model_type=default`, thinking/search flags), а не гарантия конкретной версии весов DeepSeek. Сессия новая на каждый completion, история передается текстом с метками ролей. Это не нативная структурированная семантика OpenAI system messages. Созданные сессии остаются в аккаунте; pooling/удаление удаленных сессий пока не реализовано.

Поддерживаются model, messages, stream, stream_options, store, max_completion_tokens/max_tokens, tools, tool_choice, parallel_tool_calls, temperature, top_p, user, metadata и n=1. Роли: system/developer/user/assistant/tool; content — строка или массив text parts. Assistant с tool_calls может иметь null content. Неизвестные поля и изображения по-прежнему возвращают 400. `/v1/messages` не реализован: для Anthropic нужны отдельные DTO и SSE.

`/health` проверяет только локальную работоспособность и наличие валидной конфигурации при старте. Он не проверяет актуальность токена, captcha или доступность DeepSeek. Приложение не выполняет браузерную аутентификацию. HTTP 401/403/429 upstream передаются клиенту; подробное содержимое ответа upstream не раскрывается.

## Проверки

```powershell
dotnet run --project Tests/Smoke.csproj
```

Покрыты Unicode, reasoning patches, multiline SSE, EOF, ошибка upstream, обрыв без completion marker, cancellation и pipeline session → challenge → completion через fake HttpMessageHandler. Live-запросы с вашей сессией и загрузка реального WASM при разработке не выполнялись; реальная работа с DeepSeek зависит от действительности auth и текущего Web API.

## Консольный интерфейс

При запуске — ASCII-логотип ApiOneX с анимацией появления и световым проходом, cyan/violet палитра и подпись kulsred dev :3. Курсор восстанавливается после анимации. При перенаправлении stdout или NO_COLOR используется статичный ASCII без ANSI. Баннер показывает адрес конфигурации; успешный запуск сервера подтверждается стандартным сообщением Now listening on.

## Совместимость с инструментами и параметрами клиентов

Web API DeepSeek не предоставляет нативного function calling. ChatCompatibility передает объявления function tools и историю assistant.tool_calls/tool messages в prompt, а затем преобразует специальный текстовый envelope в OpenAI tool_calls. Код прокси сам инструменты не выполняет: это делает ваш клиент.

Поддерживаются tool_choice auto/none/required и выбор именованной функции; parallel_tool_calls=false требует один вызов. В ответах возвращаются уникальные call_id, JSON-строка arguments и finish_reason=tool_calls. В SSE каждый вызов имеет index. Контент в режиме tools буферизуется до завершения, чтобы не отправить протокольный envelope как обычный текст; reasoning продолжает стримиться. Незаявленные функции, невалидный JSON и отсутствие обязательного вызова дают ошибку upstream. Полная проверка arguments по JSON Schema пока не выполняется; клиент обязан валидировать аргументы перед исполнением. Соблюдение протокола зависит от модели, это не native structured outputs/strict mode.

max_completion_tokens/max_tokens используются как инструкция модели, а не жесткий лимит генерации: X-ApiOneX-Token-Limit: prompt-hint. temperature/top_p принимаются и проверяются на допустимый диапазон, но Web API использует свои настройки: X-ApiOneX-Sampling: web-default. store/user/metadata принимаются для совместимости, локального хранилища нет (X-ApiOneX-Storage: disabled). Это не обещание управления историей на стороне DeepSeek: удаленные Web-сессии создаются как раньше.

Проверки используют fake upstream, реальные запросы к аккаунту DeepSeek не выполняются. Дополнительно покрыты параметры из клиентского запроса, tool calls в JSON и SSE, сериализация arguments, include_usage, round trip tool result, tool_choice и ошибки протокола.