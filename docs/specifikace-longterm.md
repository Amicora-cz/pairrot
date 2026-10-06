# Pairrot — dlouhodobá funkční specifikace

> Upřesnění modelu: tenant i subtenant používají stejnou entitu a do budoucna stejné platební funkce. Lektor nebo studio je subtenant NaLekci. Možnost spravovat subtenanty nebo vytvářet kampaně určuje konfigurace tenanta. Aktuální podrobnosti a pravidla přístupu stanovuje [Feature 1](./features/001-tenants.md).

## 1. Smysl produktu

Pairrot páruje příchozí bankovní převody s očekávanými platbami a oznamuje výsledek navazujícím systémům. Peníze přicházejí přímo na účty příjemců.

Základní příslib: **„Řekni nám, jakou platbu očekáváš. My ti řekneme, kdo zaplatil.“**

Prvním zákazníkem je NaLekci.cz. Pairrot má současně fungovat jako samostatný produkt pro další platformy, organizátory akcí, kluby a spolky.

Tento dokument popisuje cílové funkce. Rozsah prvního vydání určuje [krátkodobá specifikace](./specifikace-mvp.md), technické uspořádání popisuje [cílová architektura](./architektura.md). Produkční uživatelské rozhraní patří do budoucího rozvoje. První verze má pouze lokální Vite + React SPA pro testování API. Cenotvorba se řeší samostatně.

## 2. Způsoby použití

| Způsob použití | Příklad | Potřebné chování |
| --- | --- | --- |
| Trvalé používání jedním příjemcem | Klub vybírající příspěvky | Průběžné vytváření a párování očekávaných plateb. |
| Trvalé používání platformou | NaLekci a jeho lektoři či studia | Jeden zákazník Pairrotu spravuje více příjemců, každý s vlastními bankovními účty a platbami. |
| Časově omezené používání | Registrace na závod nebo tábor | Párování pro konkrétní kampaň a období, včetně dořešení pozdních plateb. |

Použití pro jednu akci má být možné bez nutnosti trvalého předplatného. Konkrétní obchodní model a ceny se stanoví samostatně.

## 3. Základní pojmy

| Pojem | Význam |
| --- | --- |
| Zákazník (tenant) | Například NaLekci nebo pořadatel závodu. Jeho data a přístupy jsou oddělené od ostatních zákazníků. |
| Příjemce | Role tenanta přijímajícího peníze. Lektor nebo studio pod NaLekci je subtenant; samostatný zákazník může být příjemcem přímo. Oba používají stejný model a vlastní konfiguraci. |
| Bankovní spojení | Konkrétní účet příjemce a způsob získávání informací o jeho příchozích platbách. Příjemce může mít více spojení. |
| Kampaň | Volitelné seskupení očekávaných plateb pro akci či období. Patří příjemci a nenahrazuje oddělení zákazníků nebo příjemců. |
| Platební požadavek | Očekávaná částka, měna, cílový účet, identifikace platby a reference do navazujícího systému. |
| Příchozí transakce | Záznam skutečné příchozí platby získaný z bankovního zdroje. |
| Přiřazení platby | Vazba mezi transakcí a požadavkem, včetně přiřazené částky. |
| Událost | Oznámení změny, například zaplacení požadavku, které lze doručit navazující aplikaci. |

Platební požadavek může existovat bez kampaně. S rezervací, registrací nebo objednávkou jej propojuje externí reference — identifikátor, který dodá navazující aplikace. Pairrot tak například ví, že platba patří k rezervaci `booking_123`, aniž by musel znát její podrobnosti.

## 4. Cílové funkce

### Správa zákazníků a příjemců

- Samoobslužné založení zákazníka a nastavení služby.
- Správa více příjemců a jejich bankovních spojení pod jedním zákazníkem.
- Přístupy členů týmu a oprávnění odpovídající jejich odpovědnosti.
- Správa integračních přístupů a míst pro doručování oznámení.

### Očekávané platby a platební podklady

- Vytváření a správa požadavků přes integraci i uživatelské rozhraní.
- Částka, měna, variabilní symbol, splatnost, externí reference a volitelná kampaň.
- Platební instrukce a QR platba pro předání plátci.
- Přehled očekávané, zaplacené a zbývající částky.
- Volitelně sdílená platební stránka nebo PDF s platebními instrukcemi.

### Příjem transakcí

- Zpočátku zpracování bankovních notifikačních e-mailů.
- Postupné rozšiřování podporovaných bank podle skutečných potřeb zákazníků.
- Později další zdroje, například bankovní API a import výpisů.
- Dohledání původu transakce, zobrazení chyb zpracování a opakované zpracování zdroje.
- Ochrana proti opakovanému započtení téže platby, včetně souběhu více zdrojů.

### Párování a řešení výjimek

- Automatické párování jednoznačných plateb.
- Návrhy kandidátů a ruční řešení nejednoznačných případů.
- Částečné platby, doplatky, přeplatky a rozdělení jedné transakce mezi více požadavků.
- Práce s chybějící nebo chybnou identifikací, pozdními platbami a opakovanými platbami.
- Ruční přiřazení, opravy a ignorování nesouvisejících transakcí s dohledatelnou historií.

### Kampaně

- Založení kampaně pro akci nebo období, nastavení začátku a konce a přehled jejího průběhu.
- Více kampaní může využívat stejné bankovní spojení příjemce.
- Pravidla pro ukončení vytváření požadavků a následné období pro pozdní platby.
- Uzavření a archivace kampaně při zachování historie.
- Ukončení kampaně nesmí zastavit párování jiných kampaní nebo běžných požadavků na stejném účtu.

### Integrace a provozní přehled

- Vytvoření požadavku a získání jeho aktuálního stavu navazující aplikací.
- Spolehlivé oznámení změn, opakované doručení při výpadku a přehled výsledků doručení.
- Oznámení oprav, které mění dříve nahlášený stav zaplacení.
- Úvodní přehled, hledání a filtrování příjemců, požadavků, transakcí a výjimek.
- Export přehledů, například pro kontrolu registrací na akci.

## 5. Trvalé produktové zásady

- Platby se přiřazují pouze uvnitř příslušného zákazníka a příjemce a vůči správnému cílovému bankovnímu účtu.
- Nejednoznačný případ vyžaduje rozhodnutí člověka. Samotná shodná částka není důvodem k automatickému označení za zaplacené.
- Stav úhrady vychází z přiřazených částek. Musí být možné vysvětlit, které transakce jej tvoří.
- Opakované doručení stejného vstupu nesmí vytvořit další úhradu.
- Změny a opravy zůstávají dohledatelné a promítají se i do navazujících systémů.
- Trvalé používání i jednorázové kampaně využívají stejné základní funkce.

## 6. Hranice produktu

Pairrot se soustředí na evidenci očekávaných plateb, jejich párování a předávání výsledků. Fakturace, účetnictví, DPH, CRM, provádění převodů a vracení peněz nejsou součástí této vize. Případné platební PDF je podklad k úhradě, nikoli účetní doklad.

## 7. Postup rozvoje

1. **Funkční provoz pro NaLekci:** API a zpracování na pozadí, více příjemců, příjem bankovních e-mailů, párování a spolehlivé předání výsledků. Správa přes API, lokální SPA pro testování.
2. **Rozšíření použití:** uživatelské rozhraní, další banky, kampaně, pohodlnější hromadná správa, exporty a pokročilejší řešení plateb.
3. **Veřejný produkt:** noví zákazníci si sami založí a nastaví službu, přidají členy týmu a aktivují párování pro trvalé nebo časově omezené použití.

Pořadí dalších funkcí se řídí ověřenými potřebami zákazníků. Cílový rozsah není závazkem implementovat všechny funkce v prvním vydání.
