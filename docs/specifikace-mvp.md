# Pairrot — krátkodobá funkční specifikace

> Implementace nyní postupuje po jednotlivých funkcích. První krok určuje anglické zadání [Feature 1: tenants, subtenants, and API access](./features/001-tenants.md). V tomto kroku používáme lokální PostgreSQL v Dockeru, bez Resendu a zpracování plateb. Lektor nebo studio je subtenant se stejným modelem jako nadřazený tenant; označení „příjemce / account“ níže popisuje jeho roli, nikoli samostatný typ entity. Práva nadřazeného tenanta a oddělení dat se řídí zadáním Feature 1. Zbytek tohoto dokumentu popisuje širší plán MVP.

## 1. Cíl prvního vydání

Zprovoznit Pairrot pro **NaLekci.cz**: NaLekci vytvoří očekávanou platbu pro konkrétního lektora nebo studio, Pairrot zpracuje bankovní oznámení, přiřadí platbu a oznámí výsledek zpět. Interní obsluha přes chráněné API vyřeší případy, které nelze rozhodnout automaticky.

První vydání zahrnuje API a zpracování na pozadí. **Pro testování vznikne pouze lokální Vite + React SPA; produkční frontend se nevytváří ani nenasazuje.** NaLekci je první zákazník samostatného produktu. Stejné funkce musí později posloužit dalším zákazníkům bez úprav šitých na míru jejich rezervacím nebo registracím.

Dlouhodobý kontext popisuje [dlouhodobá specifikace](./specifikace-longterm.md). Tento dokument určuje funkce a ověřitelné chování. Technologie a uspořádání služby popisuje [cílová architektura](./architektura.md).

## 2. Rozsah a pracovní rozhodnutí

První verze podporuje více zákazníků a příjemců, bankovní e-maily, párování a propojení s NaLekci. Správa se provádí přes API. K jeho testování slouží lokální SPA. Produkční administrační obrazovky, veřejné registrace, nákupy a účtování služby se odkládají.

Tam, kde konverzace neurčuje přesné chování, dokument navrhuje jednoduchá pravidla pro první vydání. Hlavní rozhodnutí o rozsahu jsou:

- Zpočátku platby v CZK. Jiná měna se nesmí převádět nebo párovat jako CZK.
- Automaticky se potvrzují pouze jednoznačné přesné shody. Odlišné částky a částečné platby řeší obsluha.
- Podporují se ručně přiřazené částečné platby, doplatky a přeplatky. Rozdělení jedné transakce mezi více požadavků se odkládá.
- Kampaně se zatím nezakládají ani neovládají. Požadavek funguje samostatně.
- Podporované banky určí první skutečně připojovaní příjemci NaLekci. Konverzace jejich seznam nestanovuje; zmínění banky v konkurenčním rozboru není požadavkem na její podporu.

V textu se používá několik pojmů:

- **Zákazník (tenant):** například NaLekci, které má vlastní přístup a oddělená data.
- **Příjemce (account):** konkrétní lektor nebo studio, na jehož bankovní účet chodí peníze.
- **Platební požadavek:** záznam, že od někoho očekáváme určitou částku na určitém účtu.
- **Externí reference:** identifikátor z NaLekci, například `booking_123`, podle kterého se výsledek spojí se správnou rezervací.
- **VS:** variabilní symbol platby.
- **Webhook:** automatické oznámení, které Pairrot odešle do NaLekci při změně úhrady.

## 3. Uživatelé a oddělení dat

### NaLekci jako připojená aplikace

- Má vlastní integrační přístup navázaný na zákazníka NaLekci.
- Spravuje příjemce, jejich spojení a platební požadavky.
- Získává aktuální stav požadavků a přijímá oznámení změn.
- Zákazník se určuje podle přístupu; aplikace si nemůže volbou identifikátoru zpřístupnit cizí data.

### Interní obsluha Pairrotu

- Používá chráněné API s vlastním přístupem pro správu.
- Spravuje příjemce, kontroluje zpracování, ručně řeší platby a sleduje doručování výsledků.
- Přístup má pouze k přidělenému zákazníkovi. Stačí jedna administrátorská role.

### Lektor nebo studio

- Je samostatným příjemcem uvnitř NaLekci, s vlastními bankovními spojeními a platbami.
- Pro první vydání nepotřebuje vlastní přihlášení do Pairrotu.
- Dostane instrukce pro nastavení bankovních notifikací prostřednictvím NaLekci nebo interní obsluhy.

Zákazník a interní přístupy se založí administrativně. Veřejný formulář pro založení zákazníka není potřeba. Oddělení zákazníků musí fungovat už nyní, i když v produkčním použití bude zpočátku jediný.

## 4. Hlavní průchod

1. NaLekci nebo obsluha založí příjemce a jeho bankovní spojení.
2. Pairrot přidělí spojení unikátní adresu pro příchozí bankovní oznámení.
3. Příjemce nastaví zasílání oznámení ve svém bankovnictví.
4. NaLekci vytvoří požadavek s referencí rezervace, částkou, měnou, VS a cílovým spojením.
5. Pairrot vrátí identifikátor, stav a platební podklady včetně dat pro QR platbu.
6. Plátce pošle peníze na účet příjemce; banka zašle oznámení.
7. Pairrot z oznámení vytvoří transakci a provede párování.
8. Jednoznačnou přesnou platbu přiřadí automaticky. Ostatní zpřístupní přes API k ručnímu řešení.
9. Změnu úhrady doručí NaLekci, které podle vlastní logiky aktualizuje rezervaci.

## 5. Správa příjemců a bankovních spojení

### Příjemce

- Založení, zobrazení seznamu a detailu a úprava názvu.
- Externí reference pro propojení s lektorem či studiem v NaLekci; v rámci zákazníka jednoznačná.
- Přehled jeho bankovních spojení, požadavků, transakcí a nevyřešených případů.
- Jeden příjemce může mít více bankovních spojení.

### Bankovní spojení

- Eviduje banku, číslo účtu nebo IBAN, měnu a přiřazeného příjemce.
- Má vlastní unikátní příchozí e-mailovou adresu dostupnou přes API.
- Poskytuje instrukce pro nastavení oznámení, poslední přijatou zprávu a poslední úspěšně zpracovanou platbu.
- Rozlišuje čekání na první oznámení, potvrzený příjem a problém při zpracování. Pouhá existence adresy neznamená funkční napojení.
- Chybný formát platebních údajů nelze uložit jako použitelné spojení.
- Změna cílového účtu nesmí zpětně přepsat platební instrukce existujících požadavků; nový účet se přidá jako nové spojení.

Pro první verzi se stejné bankovní konto nepoužívá pro několik různých příjemců. Pokus o takové nastavení musí skončit srozumitelnou chybou, nikoli nejasným rozdělováním transakcí.

## 6. Platební požadavky

### Vytvoření a zobrazení

NaLekci může vytvořit požadavek a získat jeho detail nebo filtrovaný seznam. Požadavek obsahuje:

| Údaj | Pravidlo |
| --- | --- |
| Příjemce a bankovní spojení | Povinné; spojení patří danému příjemci. |
| Externí reference | Povinná; identifikuje například rezervaci. Jednoznačná v rámci příjemce. |
| Očekávaná částka a měna | Povinné; kladná částka, přesnost na haléře, pro první verzi CZK. |
| Variabilní symbol | Povinný pro vytvoření v první verzi; dodává jej NaLekci. Zachovává se jako identifikátor, včetně úvodních nul. |
| Splatnost | Volitelná. Její překročení samo o sobě neznamená zrušení požadavku. |
| Popis | Volitelný text pro orientaci obsluhy. |

Na stejném bankovním účtu nelze vytvořit další požadavek se stejným VS, ani pokud je předchozí zaplacený či zrušený. Toto zúžení první verze předchází nesprávnému přiřazení pozdní platby; u různých příjemců s různými účty se VS opakovat může.

### Platební podklady

- Cílové číslo účtu nebo IBAN, částka, měna a VS.
- Data pro QR platbu odpovídající stejným údajům.
- Platební údaje a QR data poskytuje API; jejich zobrazení plátci zajišťuje NaLekci.
- Pro integraci stačí QR data; samostatné stahování obrázku, veřejná platební stránka ani PDF nejsou podmínkou vydání.

### Změny a opakované požadavky

- Opakovaný pokus o vytvoření stejného požadavku nesmí vytvořit další pohledávku.
- Stejná externí reference se stejnými údaji vrátí již existující požadavek. Pokud se údaje liší, Pairrot oznámí rozpor a původní požadavek nepřepíše.
- Po vytvoření se nemění částka, měna, cílový účet ani VS. Je možné upravit popis a splatnost.
- Chybný požadavek bez přiřazených plateb lze zrušit a vytvořit nový s novou referencí a VS.
- Požadavek s přiřazenou platbou nelze přímo zrušit. Nejprve musí obsluha vyřešit existující přiřazení.

### Stav požadavku

| Stav | Význam |
| --- | --- |
| Otevřený | Není přiřazena žádná úhrada. |
| Částečně zaplacený | Součet přiřazení je větší než nula a menší než očekávaná částka. |
| Zaplacený | Součet přiřazení odpovídá očekávané částce. |
| Přeplacený | Součet přiřazení přesahuje očekávanou částku. |
| Zrušený | Požadavek byl zrušen a není cílem automatického párování. |

Překročená splatnost je samostatný příznak u nedoplacených požadavků. Pozdní přesná platba na nezrušený otevřený požadavek se může běžně spárovat. Automatická expirace se v první verzi nezavádí.

## 7. Příjem bankovních oznámení

- Přijatá zpráva se přiřadí ke konkrétnímu bankovnímu spojení podle příchozí adresy.
- Uchová se původní zpráva a výsledek jejího zpracování, aby šlo chybu dohledat a zprávu zpracovat znovu.
- Z podporovaného oznámení se získá částka, měna, datum platby, případný VS, bankovní identifikace transakce a dostupné údaje o odesílateli či zprávě pro příjemce.
- Chybějící nepovinné údaje, například VS nebo jméno plátce, nesmějí zablokovat evidenci jinak čitelné příchozí platby.
- Zpráva bez spolehlivě rozpoznané částky nebo měny se označí k řešení a nevytvoří potvrzenou úhradu.
- Neznámý formát, nesoulad cílového účtu a neověřený původ oznámení nesmějí automaticky potvrdit platbu. Obsluha vidí důvod problému.
- Odchozí platba, oznámení o zůstatku nebo jiná zpráva se nesmějí vydávat za příchozí úhradu.
- Opakované doručení nebo opětovné zpracování stejné zprávy nesmí zdvojit transakci ani přiřazení.
- Dvě skutečné platby se stejnou částkou a VS musí zůstat dvěma platbami. Samotná shoda těchto údajů nestačí k označení za duplicitu; nejistý případ vyžaduje kontrolu.
- Transakce může přijít před vytvořením požadavku. Po vytvoření odpovídajícího požadavku se dosud nepřiřazené transakce znovu posoudí.

Před připojením konkrétní banky musí být ověřeno zpracování jejích skutečných vzorových oznámení včetně dostupných údajů pro rozpoznání duplicit a ověření původu. První verze neslibuje univerzální podporu všech bankovních e-mailů.

## 8. Automatické párování

Automatické přiřazení proběhne pouze při splnění všech podmínek:

1. Transakce je platná příchozí platba, dosud nepřiřazená a neignorovaná.
2. Požadavek patří stejnému zákazníkovi a příjemci a očekává platbu na stejný bankovní účet.
3. Souhlasí měna a VS.
4. Existuje právě jeden odpovídající otevřený požadavek bez dosavadních přiřazení.
5. Částka přesně odpovídá očekávané částce.

Pairrot přiřadí celou transakci, označí požadavek za zaplacený a vytvoří oznámení pro NaLekci. U přiřazení je vidět, zda vzniklo automaticky nebo ručně a proč.

| Situace | Chování první verze |
| --- | --- |
| Stejný VS, odlišná částka | Nabídnout požadavek ke kontrole; automaticky neměnit úhradu. |
| Chybějící nebo nerozpoznaný VS | Ponechat nepřiřazené; umožnit ruční hledání. |
| Více možných požadavků | Označit nejednoznačnost a předat obsluze. |
| Platba ke zrušenému požadavku | Předat ke kontrole bez obnovení požadavku. |
| Další platba k již zaplacenému požadavku | Předat ke kontrole jako možnou další úhradu nebo duplicitu. |
| Doplatek k částečně zaplacenému požadavku | Nabídnout k ručnímu přiřazení. |
| Shodný VS u jiného příjemce nebo na jiném účtu | Nepřiřazovat mimo správného příjemce a cílový účet. |
| Nesoulad měny | Neprovádět přiřazení ani přepočet. |

První verze nepotřebuje odhadování podle jména, textu zprávy nebo procentuálního skóre. API může vrátit kandidáty podle VS a částky; vždy musí uvést důvod nabídky.

## 9. Ruční řešení a opravy

Obsluha může přes chráněné API:

- Vyhledat požadavek podle externí reference, VS, částky nebo příjemce.
- Přiřadit celou nepřiřazenou transakci k jednomu nezrušenému požadavku stejného příjemce, cílového účtu a měny, i při chybném VS nebo odlišné částce.
- Přiřadit několik transakcí k jednomu požadavku a tím evidovat částečné úhrady a doplatek.
- Přiřadit vyšší částku a označit požadavek za přeplacený; přebytek zůstane viditelný.
- Zrušit chybné přiřazení a případně transakci přiřadit správně.
- Ignorovat nesouvisející nepřiřazenou transakci s uvedením důvodu a později ji vrátit k řešení.
- Opakovat zpracování chybné zprávy nebo párování dosud nevyřešené transakce.

Před ručním přiřazením lze přes API načíst očekávanou částku, transakci a dosavadní úhradu. Po přiřazení API vrátí výsledný stav. Ruční přiřazení a opravy mají zaznamenaný důvod, autora a čas.

Jedna transakce se v první verzi přiřazuje celá a nejvýše k jednomu požadavku. Současné automatické a ruční zpracování ji nesmí započítat dvakrát.

Zrušení přiřazení přepočítá stav požadavku a vytvoří oznámení opravy pro NaLekci. Může tedy změnit zaplacený požadavek zpět na otevřený nebo částečně zaplacený. Ručně rozpojená transakce se nesmí bez dalšího rozhodnutí obsluhy automaticky vrátit ke stejnému chybnému přiřazení.

## 10. Integrace a doručování výsledků

### Funkce dostupné NaLekci

- Založení a načtení příjemce, včetně dohledání podle externí reference.
- Založení a načtení bankovního spojení, příchozí e-mailové adresy a stavu napojení.
- Vytvoření, načtení, vyhledání a zrušení platebního požadavku podle výše uvedených pravidel.
- Získání platebních podkladů, aktuálního stavu, očekávané a uhrazené částky.
- Příjem oznámení změn úhrady přes webhook.

Každá operace je omezená přístupem zákazníka. Opakované vytváření příjemců a spojení při opakování integračního volání také nesmí nechtěně vytvářet duplicity.

### Oznámení změn úhrady

NaLekci dostává oznámení při částečné úhradě, zaplacení, přeplacení a opravě přiřazení. Každé oznámení obsahuje:

- Jedinečný identifikátor události a čas jejího vzniku.
- Typ změny a pořadí změny v rámci požadavku.
- Identifikátor příjemce a požadavku i externí referenci požadavku.
- Aktuální stav, očekávanou částku, přiřazenou částku a měnu.

Oznámení vzniká i při změně uhrazené částky, která nezmění kategorii stavu, například při druhé částečné úhradě.

### Spolehlivost a kontrola doručení

- Lze nastavit adresu, na kterou Pairrot posílá oznámení. Každé oznámení je podepsané pomocí společného tajného klíče, aby NaLekci mohlo ověřit, že pochází z Pairrotu.
- Neúspěšné doručení se automaticky opakuje. Po vyčerpání pokusů zůstane viditelné jako problém a lze jej ručně zopakovat.
- Opakované doručení stejné události zachovává její identifikátor; NaLekci podle něj zabrání opakovanému provedení stejné akce.
- Starší doručené oznámení nesmí přepsat novější stav. NaLekci může stav kdykoli ověřit přímým načtením požadavku.
- Výpadek NaLekci nevrací spárovanou platbu do nezaplaceného stavu. Stav úhrady a stav doručení jsou oddělené.
- Potvrzená změna úhrady nesmí zůstat bez evidované události k doručení.
- Přes API je dostupná historie pokusů, čas, výsledek, odpověď příjemce a plán dalšího pokusu, pokud existuje.
- Přístupový klíč pro propojení i klíč pro podpis oznámení lze vyměnit; starý přístup lze zneplatnit.

## 11. Správa přes API a lokální testovací aplikace

Produkční frontend se v první verzi nevytváří. Interní obsluha potřebuje přes chráněné API provést následující operace:

- Vyhledat, založit a upravit příjemce a načíst jeho spojení a související platby.
- Zadat bankovní spojení, získat příchozí adresu a instrukce a ověřit poslední příjem či chyby.
- Hledat požadavky podle reference a VS, filtrovat podle příjemce, stavu a splatnosti a načíst platební údaje, přiřazení a historii.
- Upravit popis či splatnost a zrušit požadavek podle pravidel.
- Načíst transakce podle příjemce, účtu, data a stavu řešení, včetně zdroje a přiřazení.
- Načíst nepřiřazené a nejednoznačné transakce, kandidáty a důvody nesouladu; přiřadit, ignorovat nebo opravit přiřazení.
- Dohledat původní zprávu, výsledek zpracování a důvod chyby a zopakovat zpracování.
- Zkontrolovat oznámení a jednotlivé pokusy o doručení a zopakovat neúspěšné doručení.
- Spravovat integrační přístup a cíl webhooku.

Každá operace rozlišuje zákazníka, příjemce a bankovní účet a podléhá kontrole přístupu. Ruční zásahy mají stejná pravidla jako automatické zpracování a zachovávají historii změn.

Pro lokální testování vznikne jednoduchá Vite + React SPA. Má umožnit:

- Zadat adresu testovaného API a použít platný přístup pro testování.
- Založit a načíst příjemce, bankovní spojení a platební požadavky.
- Zkontrolovat platební podklady, stav požadavku a příchozí transakce.
- Vyzkoušet ruční přiřazení, jeho opravu a ignorování transakce.
- Zobrazit chyby příchozích zpráv a výsledky doručení a vyžádat opakování.
- Přečíst odpověď nebo chybu API, aby šlo ověřit chování služby.

Testovací aplikace volá stejné API jako ostatní klienti. Vlastní pravidla párování ani přímý přístup do databáze nemá. Běží pouze lokálně a není součástí produkčního nasazení.

## 12. Co se nyní neimplementuje

- Produkční frontend, zákaznická administrace, přihlašovací stránka a produkční dashboard. Lokální testovací SPA do této výluky nespadá.
- Veřejná registrace, samoobslužné vytváření zákazníků a průvodce prvním nastavením.
- Ceníky, nákup kampaní, předplatné, zkušební období, vyúčtování služby a Stripe.
- Zakládání a ovládání kampaní, jejich časová aktivace a archivace.
- Týmové role, pozvánky a samostatné přístupy lektorů či studií.
- Další zdroje transakcí, například bankovní API, Open Banking a import výpisů.
- Podpora všech bank bez předem vymezeného rozsahu.
- Automatické odhadování nejasných shod, automatické doplatky a rozdělování transakce mezi více požadavků.
- Automatická expirace, vymáhání nebo upomínání nezaplacených požadavků.
- Faktury, proformy, účetní doklady, účetnictví, DPH a CRM.
- Odesílání e-mailů plátcům, veřejné platební stránky a PDF.
- Vracení peněz a provádění bankovních převodů.
- Exporty, mobilní aplikace a úpravy vzhledu pod značkou zákazníka.

## 13. Doporučené pořadí realizace

1. **Příjemci a přístupy:** administrativně založený zákazník NaLekci, přístupy k API, oddělení dat, správa příjemců a spojení.
2. **Očekávané platby:** vytvoření z NaLekci, externí reference, platební instrukce a QR data, detail a seznam přes API.
3. **Příjem bankovních e-mailů:** první potřebné banky, evidence zpráv a transakcí, chyby a ochrana před duplicitami.
4. **Párování:** přesné automatické shody, fronta výjimek, ruční přiřazení, částečné úhrady a opravy.
5. **Předání výsledků:** oznámení do NaLekci, ověření původu, opakování a kontrola doručení.
6. **Lokální testovací SPA a ověření celého průchodu:** jednoduché obrazovky pro zkoušení API, běžná platba i chybové a opravné scénáře, kontrola historie a chyb.

## 14. Podmínky dokončení prvního vydání

První vydání je funkčně hotové, pokud jsou ověřeny následující scénáře:

- NaLekci spravuje alespoň dva příjemce s odlišnými účty a stejný VS u nich nezpůsobí záměnu.
- Přístup jednoho zákazníka neumožní zobrazit ani změnit údaje testovacího druhého zákazníka.
- Vytvořený požadavek vrací správné platební instrukce a QR data; opakované vytvoření jej nezdvojí.
- Skutečné oznámení podporované banky vede přes transakci a přesné přiřazení až ke změně stavu rezervace v NaLekci.
- Duplicitní oznámení, opakované zpracování ani souběžné přiřazování nevytvoří druhou úhradu.
- Dvě různé skutečné platby se stejnou částkou a VS zůstanou evidované samostatně.
- Chybějící VS, chybná částka, neznámý formát a neověřený zdroj neoznačí rezervaci automaticky za zaplacenou a objeví se k řešení.
- Transakce přijatá před požadavkem se po jeho vytvoření znovu posoudí a může se správně přiřadit.
- Obsluha ručně přiřadí platby 500 Kč a 500 Kč k požadavku 1 000 Kč; stav přejde přes částečnou úhradu na zaplaceno.
- Obsluha přiřadí platbu 500 Kč k požadavku 450 Kč; přeplatek 50 Kč je viditelný a výsledek je oznámen NaLekci.
- Oprava přiřazení přepočítá úhradu, zachová historii a doručí opravený stav do NaLekci.
- Pozdní přesná platba se spáruje s otevřeným požadavkem; platba ke zrušenému požadavku čeká na kontrolu.
- Dočasný výpadek NaLekci vede k opakovanému doručení. Duplicitní ani opožděné oznámení nezpůsobí nesprávný stav rezervace.
- Obsluha přes API dohledá cestu od bankovní zprávy přes transakci a přiřazení až k výsledku doručení webhooku a provede ruční opravy.
- Lokálně spuštěná Vite + React SPA umožní otestovat vytvoření požadavku, kontrolu stavu a ruční opravu přes API. Do produkce se nenasazuje.

Konkrétní banky a reprezentativní vzorky jejich oznámení je nutné doplnit při zahájení implementace příjmu transakcí. Ostatní části této specifikace na jejich výběru nezávisejí.
