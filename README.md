# Fimple ATM Cash Management

Fimple uzun dönem yazılım mühendisliği stajı vaka çalışması için ASP.NET Core REST backend.
Part A ve B1–B10 uygulanmıştır. Arayüz kapsam dışında bırakılmıştır.

## Projeyi ve testleri çalıştırma

Gerekenler: Docker Engine/Desktop ve Compose; lokal geliştirme/test için .NET 10 SDK.
`global.json` kararlı .NET 10 feature band'leri arasında geçişe izin verir.

```sh
docker compose up --build -d
```

- Swagger: http://localhost:8080/swagger
- OpenAPI: http://localhost:8080/swagger/v1/swagger.json
- DB readiness: http://localhost:8080/health
- API liveness: http://localhost:8080/health/live

Compose PostgreSQL hazır olduğunda API'yi başlatır. EF Core migration'ları API
başlangıcında otomatik uygulanır; başarısız migration başlangıcı durdurur.
Veriler `cash-data` volume'ünde tutulur; `docker compose down` verileri korur.
Portlar localhost'a bağlanmıştır: API 8080, PostgreSQL 54329.

`.env.example` yerel örnek parola ve HMAC anahtarı içerir. Farklı değerler için
`.env` oluşturun; bu dosya Git'e dahil edilmez. `CARD_HASH_KEY` Base64 biçiminde
32 veya daha fazla byte olmalıdır. Aynı kartların restart sonrası tanınması için
anahtarı koruyun. Örnek anahtar gerçek kullanım için uygun değildir.

```sh
docker compose up -d db
dotnet restore
dotnet run --project src/AtmCash.Api
```

API container'ı çalışıyorsa lokal API ile port çakışmasını önlemek için önce
`docker compose stop api` kullanın. Lokal Development ayarları örnek bağlantı ve
anahtarı kullanır. Özelleştirirken `ConnectionStrings__CashDatabase` ve
`Security__CardHashKey` ortam değişkenlerini sağlayın.

```sh
dotnet test tests/AtmCash.UnitTests
docker compose up -d db
dotnet test tests/AtmCash.IntegrationTests
# Ya da DB çalışıyorken tüm testler:
dotnet test AtmCash.sln
```

Entegrasyon testleri gerçek PostgreSQL üzerinde her test sınıfı için benzersiz bir
veritabanı oluşturur, migration'ları uygular ve bitince yalnız kendi geçici
veritabanını siler. PostgreSQL erişilemiyorsa testler başarısız olur; sessizce
atlanmaz. `ATM_TEST_DATABASE` ile database oluşturma yetkili farklı bir bağlantı
sağlanabilir. Özel Compose parolası kullanıyorsanız test bağlantısını da güncelleyin.
HTTP testleri `WebApplicationFactory` kullanır; veritabanı bellekte değildir.

Migration oluşturma/kontrol:

```sh
dotnet tool restore
dotnet ef migrations add ChangeName --project src/AtmCash.Infrastructure --startup-project src/AtmCash.Api --output-dir Persistence/Migrations
dotnet ef migrations has-pending-model-changes --project src/AtmCash.Infrastructure --startup-project src/AtmCash.Api
```

## Mimari ve temel tasarım kararları

```text
src/
  AtmCash.Domain/          ATM, kaset, para, mutabakat ve düşük nakit kuralları
  AtmCash.Application/     Kullanım senaryoları, DTO, algoritma ve kalıcılık sözleşmeleri
  AtmCash.Infrastructure/  PostgreSQL/EF, migration, HMAC, outbox gönderimi
  AtmCash.Api/             HTTP, doğrulama, ProblemDetails, hosted service, Swagger/health
tests/
  AtmCash.UnitTests/       Domain, algoritma, HMAC ve eşik testleri
  AtmCash.IntegrationTests/ Gerçek PostgreSQL, HTTP, eşzamanlılık ve rollback testleri
postman/                  Tam uçtan uca ve daha küçük örnek akışlar
docs/                     Gereksinim eşleştirmesi ve geliştirme kararları
```

Domain dış katmanlardan bağımsızdır. Application Domain'e; Infrastructure
Application'a bağlıdır. API DI kayıtlarını birleştirir. HTTP istek modelleri ve
DTO'lar domain entity'lerini doğrudan değiştirmeye izin vermez. Genel amaçlı
repository, MediatR, AutoMapper, mikroservis veya ek mesaj kuyruğu kullanılmadı.

- **Banknot seçimi:** Sınırlı stokla knapsack/dinamik programlama; her erişilebilir
  tutarda minimum banknot sayısı saklanır. Tutarlar 10 birimle ölçeklenir, stoklar
  ikili paketlere ayrılır. Tam tutar yoksa en yakın pozitif alt ve limite uyan üst
  tutar döner. Greedy yaklaşımın başarısız olduğu sekiz B3 senaryosu test edilmiştir.
- **Kaset dağıtımı:** Çekim teklifinde fiziksel doluluk oranı yüksek recycling
  kaseti önce kullanılır; eşitlikte kaset numarası seçilir. Önceden ayrılmış notlar
  seçilemez. Teklifin kaset dağıtımı onayda sabittir. Yatırmada eşleşen recycling
  kasetleri numara sırasıyla, ardından karışık deposit kaseti doldurulur; fazlası
  müşteriye döner. Rezervasyon fiziksel kapasitede yer açmaz.
- **Eşzamanlılık:** Mutasyonlar PostgreSQL transaction'ında ATM satırını
  `SELECT ... FOR UPDATE` ile kilitler; envanter bundan sonra okunur. Çekimlerde
  kilit sırası idempotency anahtarı → kart → ATM'dir. Transaction kapsamlı advisory
  kart kilidi farklı ATM'lerde aynı kartın limit aşımını engeller. İptal ve expiry
  kullanımı azalttığından yalnız ATM kilidi alır. İki API örneğine paralel istekler
  gerçek DB testleriyle doğrulanmıştır.
- **Quote ve günlük limit:** Teklifler, alternatifler ve ayrılan kasetler DB'dedir.
  İki alternatif için kaset başına maksimum ihtiyaç rezerve edilir; limitten
  seçeneklerin en yüksek tutarı ayrılır. TRY 20.000, USD/EUR 1.000 günlük limitler
  ayrı izlenir. Aynı İstanbul günündeki onaylar ve henüz geçerli bütün teklifler
  sayılır. Gece yarısını geçen çekim onaylandığı güne yazılır.
- **Süre dolumu:** Teklif 60 saniyede geçersizleşir. Hosted service saniyelik
  taramayla rezervasyonları bırakır; çekim endpoint'leri ilgili ATM'de temizlik de
  yapar. Fiziksel temizlik sonraki başarılı tarama/işlemdedir; geçerlilik uzamaz.
  Expired confirm rezervasyon bırakmayı commit eder, ardından 410 döner.
- **Idempotency:** Başarılı confirm anahtarı, quote kimliği ve tutarı nakit düşümüyle
  aynı transaction'da saklanır. Sonuç kalıcı onaylanmış quote'dan üretilir. Aynı
  anahtar/istek aynı JSON'u döndürür; farklı içerik 409 verir. Yeni anahtarla aynı
  quote'u tekrar onaylamak da reddedilir. Doğrulama hataları anahtar tüketmez.
  Zamanlar UTC ve mikro saniye hassasiyetindedir; .NET/PostgreSQL round-trip farkı
  ilk yanıtla tekrar yanıtını değiştirmez.
- **Kart verisi:** Yalnız HMAC-SHA256 parmak izi saklanır. PAN/hash API yanıtına
  yazılmaz; istek gövdeleri ve hassas EF parametreleri loglanmaz.
- **Ledger:** LOAD, UNLOAD, WITHDRAWAL, DEPOSIT, RESERVATION, RESERVATION_RELEASE
  ve RECONCILIATION_ADJUSTMENT stokla aynı transaction'da kaydedilir. Fiziksel ve
  rezervasyon deltaları ayrıdır. EF guard'ı ve DB trigger'ları mevcut hareketlerde
  UPDATE/DELETE/TRUNCATE'i reddeder. `ReferenceId` quote, yatırma veya mutabakatı
  ilişkilendirir. Onay tüm rezervasyonu bırakır ve yalnız seçilen notları düşer.
- **Mutabakat:** Beş kasetin tam sayımı önce doğrulanır. Farkların mutlak TRY
  değerleri toplanır; tam 1.000 otomatik uygulanır, üzeri farklı X-User onayı bekler.
  Beklerken cash/config/durum değişimi dondurulur. Kur her satırda snapshot olarak
  saklanır. Onay aynı ATM kilidinde bir kez uygulanır; eski baseline reddedilir.
  Fark yoksa audit kaydı tutulur, sıfır delta hareketi eklenmez.
- **Rapor ve tutarlılık:** REPEATABLE READ snapshot'ı stok, kurlar ve ledger
  okumalarının aynı durumu görmesini sağlar. Tutarlılık SQL'de bütün hareketleri
  toplar; saklanan not/rezerve miktarlarıyla karşılaştırır. Eksik stok satırlarını
  da raporlar. ATM/ağ raporu para birimi toplamlarını ve TRY değerlerini içerir.
- **Düşük nakit/outbox:** Kullanılabilir recycling stoğu 10'un altına indiğinde
  olay ve kasetin uyarı durumu aynı cash transaction'ında yazılır. İlk yüklemede
  düşük olan kaset de olay üretir. Stok 10'a dönünce uyarı tekrar kurulabilir.
  Dispatcher `FOR UPDATE SKIP LOCKED` ile 100 olaylık batch alır, yapılandırılmış
  loga yazar ve SentAt'ı commit eder. Hata halinde olaylar tekrar denenir. Cash ve
  outbox rollback'i, iki dispatcher ve hosted gönderim gerçek DB'de test edilir.
- **Hata modeli:** Yapısal doğrulama 400, bulunamayan kaynak 404, durum/onay
  çatışması 409, expired quote 410, iş kuralı 422; eksik kur 503'tür. Hatalar
  `application/problem+json`, sabit `code` ve `traceId` içerir. Beklenmeyen hata
  ayrıntıları istemciye verilmez.

## API ve Postman

| İstek | Görev |
| --- | --- |
| POST /api/atms | ATM oluştur |
| GET /api/atms/{id} | Stok/durum oku |
| PUT /api/atms/{id}/configuration | Dört recycling kasetini yapılandır |
| POST /api/atms/{id}/loads veya /unloads | Elle nakit ekle/çıkar |
| PUT /api/atms/{id}/state | Hizmet/bakım geçişi |
| POST /api/atms/{id}/withdrawals/quotes | Teklif ve rezervasyon |
| GET veya DELETE /api/atms/{id}/withdrawals/quotes/{quoteId} | Oku/iptal et |
| POST /api/atms/{id}/withdrawals/quotes/{quoteId}/confirm | Tutarı onayla; Idempotency-Key zorunlu |
| POST /api/atms/{id}/deposits | Banknot dağılımı, taşma ve iadeler |
| POST /api/atms/{id}/reconciliations | Beş kaset sayımı; X-User zorunlu |
| GET /api/atms/{id}/reconciliations/{reconciliationId} | Audit/sayım/farklar |
| POST /api/atms/{id}/reconciliations/{reconciliationId}/approve | Farklı X-User ile onay |
| GET /api/atms/{id}/consistency | Ledger/stok farkları |
| GET /api/atms/{id}/report | ATM raporu |
| GET /api/reports/operations | Ağ raporu |

`postman/atm-cash-flow.postman_collection.json` tam teslim senaryosudur:
oluştur → konfigüre et → yükle → hizmete al → quote → confirm → deposit → bakım
→ reconcile → farklı kullanıcı onayı → consistency → report → hizmete dönüş.
21 adım aynı zamanda onay tekrarı, iptal, taşma/iade, aynı kullanıcı reddi ve
onay beklerken hizmete dönüşün reddini kontrol eder. Runner ile sırayla çalıştırın.
`baseUrl` varsayılan localhost:8080'dır; atmId, quoteId, reconciliationId ve
her çalıştırmaya özel idempotencyKey otomatik atanır. Confirm 60 saniye içinde yapılır.
`atm-setup` ve `atm-withdrawals` koleksiyonları daha küçük örneklerdir.

Yatırma isteği:

```json
{
  "notes": [{ "currency": "TRY", "denomination": 100, "count": 6 }],
  "unrecognizedCount": 2
}
```

Mutabakatta `cassettes` beş eleman içerir; her eleman `number` ve para birimi/kupür/
`count` içeren `notes` dizisidir. Boş notes dizisi boş kaset demektir. Karışık
kaset bütün sayılan not türlerini içerir. Tam örnekler Swagger ve koleksiyondadır.

## Varsayımlar

- Kaset numaraları sabit: 1–4 recycling, 5 deposit-only; kapasiteleri 100 nottur.
- İsimler benzersiz olmak zorunda değildir; ATM kimliği GUID'dir.
- İlk nakit yüklemesinde en az bir not gerekir; mutabakat başlangıç yüklemesini
  atlama yolu değildir. LOADED durumunda sonraki adım IN_SERVICE'dir.
- Aktif rezervasyon varken bakıma geçilmez. Fiziksel not içeren kasetin kupürü/
  para birimi ancak boşaltıldıktan sonra değiştirilebilir.
- Kalan limiti aşan çekim isteği reddedilir. Sıfır alt alternatif sunulmaz.
- Kart girişi boşluksuz 13–19 ASCII rakamdır; Luhn kontrolü istenmemiştir.
- Cash sayıları/tutarlar tam sayıdır; TRY dönüşümü decimal kullanır. Seed kurlar
  TRY=1, USD=35, EUR=40 örnek değerlerdir, canlı piyasa kuru değildir. Kurlar
  ExchangeRates tablosunda tutulur; mutabakat geçmişi sonradan kur değişiminden etkilenmez.
- Yatırmada tekrarlanan türler birleştirilir; ortak deposit kapasitesinde grupların
  ilk görülme sırası kullanılır. 60 giriş grubu sınırı vardır; sığmayan büyük
  sayılar long toplamlarla döner. Yanıt seri numarası yerine kaset bazında sayılar verir.
- Tanınmayan/returned notlar stok veya ledger'a girmez. Yalnız tanınmayan not
  içeren istek kabul edilir, tamamen boş istek reddedilir.
- Confirm dışındaki POST'lar tekrar gönderilirse yeni işlemdir. İptal tekrarlanabilir.
- X-User trim edilir ve onayda harf duyarsız karşılaştırılır; kimlik doğrulama istenmemiştir.
- Mutabakat eşiği net fark yerine mutlak fark toplamıdır. Yüksek fark onaydan
  sonra uygulanır. Onaylanmış mutabakat tekrar onaylanamaz.

## Bitmemiş işler ve bilinen sınırlamalar

Vakadaki Part A ve B1–B10 için bitmemiş özellik yoktur. Üretim kapsamına ait
sınırlar: authentication/card ownership, gerçek donanım ve müşteri hesap entegrasyonu,
canlı kur servisi, anahtar rotasyonu ve ağ raporunda pagination uygulanmadı.
Bunlar vaka için talep edilmedi. Outbox log teslimi en az bir kezdir; log ile
SentAt commit'i arasındaki çökmede aynı olay yeniden loglanabilir. Önceki audit ve
ledger kayıtları silinmez; retention/archival politikası eklenmedi. Yatırma yanıtı
kabul hareketlerini ilişkilendirir, ayrı deposit geçmişi endpoint'i bulunmaz.

## Doğrulama

12 Ekim 2026: 82 unit ve 46 gerçek PostgreSQL entegrasyon testi geçti, atlanan test
olmadı. B3'teki sekiz kabul testi; iki API örneğiyle quote/yatırma; farklı ATM'lerde
kart limiti; idempotency; expiry/gece yarısı; mutabakat/onay; UNLOAD; rapor/ledger;
outbox rollback, retry ve iki dispatcher senaryoları dahildir. Migration/model
uyumu kontrol edildi. Docker üzerindeki 21 adımlık koleksiyon akışı geçti:
setup, çekim/onay tekrarı, yatırma/iadeler, iptal, bakım, farklı kullanıcıyla
mutabakat onayı, ledger tutarlılığı ve operasyon raporu doğrulandı. Beklenen
409 yanıtları da kontrol edildi. Swagger ve health HTTP 200 döndü; derleme
sıfır uyarı ve sıfır hatayla tamamlandı. Docker'da düşük nakit olayı outbox'a
yazıldı ve arka plan servisi tarafından işlendi (`SentAt` dolu).

## AI kullanımı

OpenAI Codex gereksinim analizi, tasarım, uygulama, test ve dokümantasyon için
kullanıldı. Üretilen çıktı derleme, unit, gerçek DB ve container akışlarıyla kontrol
edildi. Düzeltilen noktalar: EF runtime/design sürümleri, OpenAPI güvenlik düzeltmesi,
MVC record validation metadata'sı, ProblemDetails içerik türü, .NET/PostgreSQL
zaman hassasiyeti, karışık kaset kapasitesi ve duplicate sayılarda taşma. Mutabakat
onayı ve outbox için yarış/rollback testleri eklendi; test yardımcıları ve stok/zaman
kodları tekrar kullanılacak biçimde ayrıldı. Kod ve test kararları Codex ile iteratif
olarak gözden geçirildi; AI çıktısı doğrulanmadan teslim edilmiş sayılmadı.
