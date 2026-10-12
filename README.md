# ATM Cash Management

Fimple yazılım mühendisliği stajı vaka çalışması için ASP.NET Core backend.
Kullanıcı arayüzü kapsam dışındadır. Geliştirme aşamalı ilerliyor; mevcut kapsam
ve kalan işler aşağıda açıkça belirtilmiştir.

## Projeyi ve testleri çalıştırma

Gerekenler: Docker Desktop/Engine ve Compose. Lokal geliştirme ve testler için
.NET 10 SDK gerekir. `global.json` .NET 10 feature band'leri arasında geçişe izin verir.

```sh
docker compose up --build -d
```

- Swagger: http://localhost:8080/swagger
- PostgreSQL readiness: http://localhost:8080/health
- API liveness: http://localhost:8080/health/live
- OpenAPI JSON: http://localhost:8080/swagger/v1/swagger.json

Compose veritabanı hazır olmadan API'yi başlatmaz. API başlangıçta EF Core
migration'larını uygular; migration başarısızsa başlangıç durdurulur.
Veriler `cash-data` volume'ünde saklanır. `docker compose down` verileri korur.
Varsayılan parola yalnızca yerel örnek içindir; `.env.example` üzerinden `.env`
oluşturup `POSTGRES_PASSWORD` değiştirilebilir. `.env` Git'e dahil edilmez.
Kart kimliği için `CARD_HASH_KEY` kullanılır: Base64 biçiminde en az 32 byte
olmalıdır. Örnek anahtar yalnızca lokal geliştirme içindir. Gerçek kullanımda
rastgele bir anahtar sağlayın ve restart'larda aynı değeri koruyun; anahtar
rotasyonu bu aşamada uygulanmamıştır. Eksik veya geçersiz anahtar API başlangıcını
durdurur. Lokal `dotnet run` geliştirme ayarındaki örnek anahtarı kullanır.

API'yi container dışında geliştirmek için:

```sh
docker compose up -d db
dotnet restore
dotnet run --project src/AtmCash.Api
```

API container'ı da çalışıyorsa önce `docker compose stop api` kullanın;
ikisi de 8080 portunu kullanır.

```sh
dotnet test tests/AtmCash.UnitTests
docker compose up -d db
dotnet test tests/AtmCash.IntegrationTests
```

Entegrasyon testleri gerçek PostgreSQL üzerinde her test sınıfı için benzersiz
bir veritabanı oluşturur, migration'ları API başlangıcında uygular ve bitince bu
geçici veritabanını siler. Varsayılan bağlantı Compose'un 54329 portuna gider.
Alternatif sunucu için `ATM_TEST_DATABASE` ortam değişkenine veritabanı
oluşturma yetkisi olan bir PostgreSQL bağlantısı verin. Özel Compose parolası
seçtiyseniz test bağlantısını da aynı parolayla ayarlayın. PostgreSQL erişilemezse
testler başarısız olur; sessizce atlanmaz. Test API'si `WebApplicationFactory`
üzerinden bellekte barındırılır; veritabanı bellekte değildir.

Migration oluşturma:

```sh
dotnet tool restore
dotnet ef migrations add ChangeName --project src/AtmCash.Infrastructure --startup-project src/AtmCash.Api --output-dir Persistence/Migrations
```

## Mimari ve temel teknik kararlar

```text
src/
  AtmCash.Domain/          ATM, kaset, stok ve hareket kuralları
  AtmCash.Application/     Kullanım senaryoları, DTO, banknot seçimi, kalıcılık sözleşmesi
  AtmCash.Infrastructure/  EF Core, PostgreSQL, mapping ve migration
  AtmCash.Api/             HTTP, doğrulama, ProblemDetails, Swagger ve health
tests/
  AtmCash.UnitTests/       DB gerektirmeyen domain ve algoritma testleri
  AtmCash.IntegrationTests/ Gerçek PostgreSQL ve HTTP akışları
docs/
  implementation-plan.md  Gereksinim eşleştirmesi ve geliştirme sırası
postman/                  Setup ve çekim akışlarının koleksiyonları
```

Domain dış katmanlara bağımlı değildir. Application Domain'e, Infrastructure
Application'a bağımlıdır. API dependency injection kayıtlarını birleştirir.
Domain nesnelerinin alanlarını HTTP istemcisi doğrudan değiştiremez; API ayrı
istek modelleri ve yanıt DTO'ları kullanır.

- **Banknot seçimi:** Stok sınırlı knapsack/dinamik programlama. Her erişilebilir
  tutar için minimum banknot sayısı tutulur. Tutarlar 10 birimle ölçeklenir;
  stoklar ikili paketlere ayrılır. Önce tam tutar, yoksa en yakın pozitif alt
  tutar ve limite uyan üst tutar bulunur. Seçim bileşeni para biriminden bağımsızdır;
  çağıran kullanım senaryosu ilgili para biriminin stoğunu ve kalan limitini
  sağlar. Kaset dağıtımı quote anındaki fiziksel doluluk oranına göre yapılır;
  eşitlikte kaset numarası kullanılır. Önceden ayrılmış banknotlar seçilemez.
- **Eşzamanlılık:** Her ATM değişimi PostgreSQL transaction'ı içinde ATM satırını
  `SELECT ... FOR UPDATE` ile kilitler; envanter bundan sonra okunur. Süreç içi
  kilitlere bağlı değildir. Çekimlerde transaction kapsamlı advisory kilitler
  `idempotency anahtarı → kart → ATM` sırasıyla alınır. Kart kilidi farklı ATM'lerde
  günlük limit aşımını engeller; anahtar kilidi aynı anahtarın farklı teklifler
  için eşzamanlı kullanılmasını engeller. İptal ve süre dolumu kullanımı yalnızca
  azalttığından sadece ATM kilidini alır. B6 testi iki API örneğine 20 paralel
  quote göndererek aynı stoktan en fazla dört banknot ayrılabildiğini doğrular.
- **Rezervasyon ve günlük limit:** Tam teklif veya iki alternatif DB'de saklanır.
  Kaset başına seçeneklerdeki en yüksek ihtiyaç rezerve edilir. Günlük limitten
  seçeneklerin en yüksek tutarı ayrılır. Limitler TRY 20.000, USD/EUR 1.000'dır;
  para birimleri ayrı izlenir. Aynı gün onaylanan tutarlar ve tüm geçerli teklifler
  sayılır. Gece yarısını geçen bir teklif onaylandığı İstanbul gününe yazılır.
  Tüm zamanlar UTC tutulur; `TimeProvider` ile sınır durumları beklemeden test edilir.
  Çekim işlem zamanları mikro saniye hassasiyetine getirilir; .NET'in 100ns tick'leri
  PostgreSQL'e yazılıp okunduğunda ilk yanıt ile idempotent tekrar arasında fark yaratmaz.
- **Süre dolumu:** Teklif tam 60 saniyede geçersiz olur. Hosted service saniyelik
  taramayla süresi dolan rezervasyonları bırakır; teklif oluşturma/okuma/onay/iptal
  de ilgili ATM'nin süresi dolan rezervasyonlarını temizler. Fiziksel serbest
  bırakma bir sonraki başarılı tarama/işlemde gerçekleşir; mantıksal geçerlilik
  tarama gecikse de uzamaz. Servis DB'deki kayıtları kullandığı için restart sonrası
  temizlik devam eder. Süresi dolmuş confirm için rezervasyon bırakma commit
  edilir, ardından 410 hatası döner.
- **Ledger:** Yükleme LOAD; çekim RESERVATION, RESERVATION_RELEASE ve WITHDRAWAL
  hareketlerini stok değişimleriyle aynı transaction'da kaydeder. `ReferenceId`
  ilgili quote'u gösterir. Onayda tüm rezervasyon bırakılır, yalnız seçilen tutar
  düşülür. İptal/süre dolumu fiziksel bakiyeyi değiştirmez.
  Fiziksel stok farkı ile rezervasyon farkı ayrı alanlardır. EF değişiklik
  kontrolü ve PostgreSQL trigger'ları mevcut hareketlerin değiştirilmesini,
  silinmesini veya tabloyu TRUNCATE etmeyi reddeder. Diğer hareket türleri için
  işlem akışları ilerleyen aşamalarda eklenecektir.
- **Hata modeli:** Model doğrulama 400, bulunamayan ATM 404, durum çatışması
  409, süresi dolmuş teklif 410, iş kuralı ihlali 422 döner. `application/problem+json` içinde sabit `code`
  ve `traceId` bulunur. Beklenmeyen hataların ayrıntıları istemciye verilmez.
- **Test edilebilirlik:** Zaman Application'a `TimeProvider` olarak enjekte
  edilir. Banknot algoritması EF/HTTP bağımlılığı olmadan test edilir. Gereksiz
  generic repository, MediatR, AutoMapper veya mikroservis altyapısı yoktur.
- **Idempotency:** Başarılı confirm'in anahtarı, quote kimliği ve seçilen tutarı
  DB'de nakit düşümüyle birlikte commit edilir. Sonuç kalıcı onaylanmış quote'dan
  üretilir. Aynı anahtar/istek aynı sonucu döndürür; süre dolumu, bakım veya başka
  API örneği bu tekrar sonucunu değiştirmez. Anahtar farklı istek için kullanılırsa
  409 `idempotency_conflict` döner. Başarılı onayı yeni anahtarla tekrarlamak da
  reddedilir. Doğrulama hataları bir anahtarı tüketmez. Anahtar tüm confirm
  endpoint'leri için globaldir; istemci her yeni onay için UUID kullanmalıdır.
- **Kart verisi:** Yalnız HMAC-SHA256 parmak izi saklanır. İstek gövdeleri ve
  hassas EF parametreleri loglanmaz; PAN veya hash API yanıtında bulunmaz.

## Varsayımlar

Tam liste ve henüz uygulanmayan özelliklere ilişkin kararlar
[uygulama planında](docs/implementation-plan.md) bulunur.

Mevcut akışta kaset numaraları sabittir: 1–4 recycling, 5 deposit-only.
Konfigürasyon dört recycling kasetini birlikte içerir. İlk konfigürasyon ve
nakit yükleme ATM durumunu ilerletir; istemci bu durumları doğrudan atlayamaz.
Yüklü bir kasetin para birimi veya kupürü değiştirilemez. Elle yüklemede kapasite
aşımı tüm isteği reddeder; B7'deki otomatik taşma yalnızca müşteri yatırması için
geçerlidir. İlk yüklemede en az bir banknot gerekir. ATM adlarının benzersiz olması
zorunlu değildir; kimlik GUID'dir. Konfigürasyon ve elle yükleme yeniden gönderilirse
idempotent olduğu varsayılmaz; HTTP POST loads her başarılı çağrıda nakit ekler.
Kart girişi boşluksuz 13–19 ASCII rakamdır; Luhn kontrolü vaka kapsamında istenmedi.
Kalan günlük limiti aşan istek reddedilir. Confirm için istemci teklifin sunduğu
tutarlardan birini açıkça seçmelidir. İptal tekrarlanabilir; iptal/süre dolumu sonrası
yanıttaki `reservedAmount` sıfırdır. Teklif banknot dağıtımı sabittir; onay anında
başka teklifler doluluk oranlarını değiştirmiş olsa da ayrılan kasetler kullanılır.

## Tamamlananlar, bitmemiş işler ve bilinen sınırlamalar

Mevcut ilk iki aşama:

- ATM oluşturma/okuma; dört recycling ve bir deposit kaseti.
- TRY/USD/EUR, 20/50/100/500; konfigürasyon, 100 banknot kapasitesi.
- İlk yükleme, hizmet/bakım geçişleri ve anlamlı durum hataları.
- Atomik elle yükleme ve değiştirilemez LOAD kayıtları.
- B3 algoritması; sekiz kabul senaryosu ve ek sınır/optimalite testleri.
- EF code-first migration, PostgreSQL, Docker, OpenAPI, ProblemDetails, health.
- Gerçek PostgreSQL setup, rollback, paralel yükleme ve ledger değişmezliği testleri.
- Quote/get/confirm/cancel; 60 saniye rezervasyon ve hosted expiration worker.
- Kart HMAC, para birimi bazında günlük limit ve İstanbul gün sınırı.
- Kalıcı idempotency; tekrar ve eşzamanlı onayda tek nakit düşümü.
- Gerçek PostgreSQL üzerinde iki API örneğiyle B6 ve farklı ATM'lerde kart limiti testleri.

Henüz tamamlanmadı: müşteri yatırması, boşaltma,
mutabakat/onay, kur tablosu, tam operasyon raporu, ledger tutarlılık endpoint'i,
düşük nakit/outbox ve outbox gönderim hizmeti. Postman koleksiyonları setup ve
çekim akışlarını kapsar; yatırma/mutabakat/raporu da içeren tam uçtan uca teslim
senaryosu sonraki aşamada tamamlanacaktır.
Bu sürüm henüz vaka tesliminin tamamı değildir.

12 Ekim 2026 doğrulaması: 53 unit ve 24 gerçek PostgreSQL entegrasyon testi geçti;
hiçbir test atlanmadı. Testler otomatik migration, günlük limitler, iki API örneğiyle
paralel quote, idempotency, ledger replay, background expiration ve gece yarısı
senaryolarını kapsar. Docker Compose üzerinden ikinci migration otomatik uygulandı;
container'daki API'de 12 adımlı setup/quote/confirm/replay/cancel akışı doğrulandı.
İlk onay ve tekrar onayın JSON yanıtı birebir aynıydı; envanter yalnızca bir kez
azaldı. Swagger ve health endpoint'leri HTTP 200 döndü. Derleme sıfır uyarı ve
sıfır hatayla tamamlandı.

## Postman

`postman/atm-setup.postman_collection.json` dosyasını içe aktarın. Koleksiyonun
`baseUrl` değişkeni varsayılan olarak `http://localhost:8080`'dır. Runner ile sırayla
çalıştırın: oluştur → konfigüre et → yükle → hizmete al → oku → bakıma al.
İlk isteğin testi `atmId` değişkenini otomatik doldurur; her çalıştırma yeni ATM açar.

`postman/atm-withdrawals.postman_collection.json` ayrıca quote, confirm, aynı
confirm'in tekrarı, envanter, ikinci quote'un iptali ve bakıma dönüşü içerir.
`quoteId` ve her çalıştırmaya özel `idempotencyKey` otomatik atanır. Tekliften sonra
confirm'i 60 saniye içinde çalıştırın; Runner bu akışı sırayla yürütür.

Çekim API'si:

| İstek | Görev |
| --- | --- |
| `POST /api/atms/{atmId}/withdrawals/quotes` | `{ "cardNumber": "4000000000000001", "currency": "TRY", "amount": 160 }` |
| `GET /api/atms/{atmId}/withdrawals/quotes/{quoteId}` | Teklif durumunu ve seçenekleri oku |
| `POST /api/atms/{atmId}/withdrawals/quotes/{quoteId}/confirm` | `{ "amount": 160 }`, zorunlu `Idempotency-Key` header'ı |
| `DELETE /api/atms/{atmId}/withdrawals/quotes/{quoteId}` | Teklifi iptal et |

## AI kullanımı

OpenAI Codex gereksinim analizi, başlangıç tasarımı, kod ve test üretimi,
belgelendirme için kullanıldı. Üretilen çıktı derleme ve gerçek DB testleriyle
kontrol edildi. Düzeltmeler: EF çalışma zamanı/design paketlerinin sürümleri
eşitlendi, OpenAPI'nin güvenlik düzeltmesi içeren sürümü sabitlendi, karışık
kaset kapasitesi batch toplamıyla doğrulandı ve ledger koruması DB'ye taşındı.
HTTP request record'larında MVC'nin gerektirdiği constructor parametresi
doğrulama metadata'sı kullanılarak entegrasyon testinde bulunan hata düzeltildi.
İkinci aşamada aynı header/isteğin paralel yürütülmesi, farklı ATM'lerde ortak
kart limiti, gece yarısı ve expiry hata transaction'ı testlerle kontrol edildi.
Model doğrulama hatasının içerik türünü değiştiren controller `Produces`
tanımı kaldırılarak ProblemDetails sözleşmesi tutarlı hale getirildi.
Canlı container akışında bulunan zaman hassasiyeti farkı 100ns tick içeren
regresyon testiyle yeniden üretildi; ilk onay ve DB'den tekrar yanıtının birebir
eşit olması için işlem zamanları mikro saniye hassasiyetinde standartlaştırıldı.
İlerleyen aşamalarda yapılan düzeltmeler bu bölümde güncellenecektir.
