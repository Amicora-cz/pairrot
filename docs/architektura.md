# Pairrot — cílová architektura

## Aktuální krok: Feature 1

Aktuální zadání je [Feature 1: tenants, subtenants, and API access](./features/001-tenants.md), se samostatným [SQL schématem](./features/001-tenants-schema.sql). Pro tento krok poběží PostgreSQL lokálně v Dockeru. Neon a Resend zatím nejsou potřeba. Lokální Vite + React SPA slouží pouze k testování API. Níže popsané produkční uspořádání zůstává cílem pro další kroky.

Navazující [Feature 002](./features/002-user-login-and-sessions.md) přidá přihlášení uživatele e-mailem a heslem, serverově ověřované cookie sessions a správu klíčů z lokální SPA. Integrační API klíče z Feature 1 budou fungovat i bez uživatelského přihlášení.

[Feature 003](./features/003-bank-configuration-and-inbound-address.md) přidá nastavení bankovních účtů a vytvoření testovací adresy pro bankovní oznámení. Resend v tomto kroku nahradí jednoduchý lokální stub; skutečný příjem e-mailů a zpracování plateb přijdou později.

[Feature 004](./features/004-payment-qr-generation.md) přidá generování platebních QR kódů pro uložený účet. API vrátí platební data nebo PNG, lokální SPA umožní náhled a stažení. Evidence očekávaných plateb a párování zůstávají na další kroky.

## Základní uspořádání

Pairrot poběží na VPS jako jedna aplikace ASP.NET Core v Docker Compose. Aplikace poskytne API a ve stejném procesu bude pomocí `BackgroundService` zpracovávat práci na pozadí. Data budou v PostgreSQL u Neonu. Příchozí e-maily přijme Resend a oznámí je Pairrotu přes webhook.

```text
VPS
└── Docker Compose
    └── pairrot
        └── ASP.NET Core
            ├── API
            └── BackgroundService

Neon
└── PostgreSQL

Resend
└── příchozí e-mail → webhook Pairrotu

Lokální vývoj a testování
└── Vite + React SPA
    └── volání API Pairrotu
```

**Frontend bude pouze lokální Vite + React SPA pro testování.** Umožní zkoušet API, kontrolovat platby a provádět ruční zásahy. Nebude se nasazovat do produkce ani zpřístupňovat zákazníkům. Produkční aplikace `pairrot` poskytuje API a zpracování na pozadí.

## Co má která část na starosti

| Část | Úloha |
| --- | --- |
| VPS a Docker Compose | Provoz aplikace `pairrot`. |
| ASP.NET Core API | Požadavky z NaLekci, správa dat, ruční zásahy a příjem webhooků z Resendu. |
| BackgroundService | Zpracování přijatých zpráv, párování transakcí a doručování výsledků do NaLekci, včetně opakovaných pokusů. |
| Neon PostgreSQL | Trvalé uložení zákazníků, příjemců, bankovních spojení, požadavků, zpráv, transakcí, přiřazení a historie doručování. |
| Resend | Příjem bankovních e-mailů a oznámení o jejich přijetí do Pairrotu. |
| Lokální Vite + React SPA | Testovací obrazovky pro volání API a kontrolu výsledků. Běží na počítači vývojáře. |

API a práce na pozadí tvoří jednu nasazovanou aplikaci. Databáze je mimo VPS, u Neonu.

## Průchod platby

1. NaLekci přes API vytvoří očekávanou platbu a získá platební podklady.
2. Banka pošle oznámení na adresu přiřazenou bankovnímu spojení příjemce.
3. Resend přijme e-mail a zavolá webhook Pairrotu.
4. API ověří původ volání a uloží oznámení do PostgreSQL jako práci ke zpracování. Úspěšné přijetí potvrdí až po uložení.
5. BackgroundService získá a uloží obsah zprávy, rozpozná bankovní údaje a vytvoří příchozí transakci. Neúspěšné získání nebo zpracování zprávy zůstane evidované pro další pokus či ruční řešení.
6. Párování buď přiřadí platbu, nebo ji ponechá k ručnímu řešení podle [funkčních pravidel](./specifikace-mvp.md).
7. Změna úhrady se uloží společně s oznámením určeným pro NaLekci.
8. BackgroundService odešle oznámení do NaLekci a zaznamená výsledek doručení.

Webhook z Resendu oznamuje přijetí e-mailu. Webhook do NaLekci oznamuje změnu úhrady. Jde o dvě samostatná propojení.

## Rozpracovaná práce a opakování

Čekající práce a její stav se ukládají do PostgreSQL. Po restartu aplikace BackgroundService naváže na uložené záznamy. Samotná paměť procesu nestačí jako evidence zpráv nebo oznámení čekajících na odeslání.

Opakované přijetí stejného oznámení ani opakovaný běh zpracování nesmějí vytvořit další úhradu. Neúspěšné doručení do NaLekci se zopakuje podle uloženého stavu. Tyto požadavky platí i při souběhu automatického zpracování a ručního zásahu přes API.

## Správa a lokální testování

NaLekci využívá API pro běžné operace. Interní obsluha přes chráněné API načte nevyřešené transakce, přiřadí platbu, opraví přiřazení nebo zopakuje zpracování a doručení. Přímé přepisování databáze není běžný způsob správy, protože by obcházelo pravidla párování a historii změn.

API vrací také chyby zpracování, historii přiřazení a výsledky doručení. Platební podklady a QR data poskytuje NaLekci.

Lokální SPA je jednoduchý testovací klient tohoto API. Umožní založit testovacího příjemce a platební požadavek, načíst transakce, vyzkoušet ruční přiřazení a zkontrolovat zprávy i doručování oznámení. Používá stejná pravidla přístupu jako jiný klient API; nečte ani neupravuje databázi přímo.

SPA se spouští lokálně přes Vite a adresa API se nastavuje podle testovaného prostředí. Přístupové údaje k Neonu a Resendu ani klíče pro podpis webhooků do ní nepatří. Produkční Docker Compose ji nespouští a ASP.NET Core ji v produkci neservíruje. Produkční vzhled, zákaznické přihlašování a veřejná administrace nejsou součástí tohoto testovacího klienta.

Rozsah funkcí určuje [krátkodobá specifikace](./specifikace-mvp.md). Budoucí uživatelské rozhraní a rozvoj produktu popisuje [dlouhodobá specifikace](./specifikace-longterm.md).
