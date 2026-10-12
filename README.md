# Fimple ATM Cash Management

Bu proje, ATM'lerin TRY, USD ve EUR cinsinden nakit yönetimini sağlayan bir REST
API'dir. Para çekme, yatırma, banknot rezervasyonu, fiziksel sayım ile sistem
kayıtlarının mutabakatı ve operasyon raporlarını kapsar. Fimple yazılım
mühendisliği stajı vaka çalışması için geliştirilmiştir.

**Teknolojiler:** ASP.NET Core (.NET 10), Entity Framework Core, PostgreSQL ve Docker Compose.

**Son doğrulama — 12 Ekim 2026:** 82 birim ve 46 PostgreSQL entegrasyon testi
olmak üzere **128 test geçti**. Docker üzerinde **21 adımlık uçtan uca akış**
doğrulandı; derleme sıfır hata ve sıfır uyarıyla tamamlandı.

Vakadaki Part A ve B1–B10 kapsamının hangi senaryolarla doğrulandığı
[uygulama planında](docs/implementation-plan.md#gereksinim-ve-doğrulama-eşleştirmesi)
yer alır. Teslim kapsamı backend'dir.

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
HTTP testleri `WebApplicationFactory` kullanır. Test kapsamı ve sonuçlar
[doğrulama bölümünde](#doğrulama), migration komutları
[tasarım belgesinde](docs/design-decisions.md#migration-yönetimi) yer alır.

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

İş kuralları, HTTP ve veritabanı bağımlılığı olmayan Domain katmanında tutulur;
böylece doğrudan birim testleriyle kontrol edilebilir. Application kullanım
senaryolarını koordine eder, Infrastructure kalıcılığı sağlar. API bağımlılıkları
birleştirir ve HTTP sözleşmesini yönetir. Kullanım senaryolarına özel kalıcılık
sözleşmeleri, transaction ve kilit kapsamını görünür tutar.

- **Dinamik programlama:** Sınırlı stokta greedy seçimi geçerli bir kombinasyonu
  kaçırabilir. Algoritma erişilebilir tutarlar için minimum banknot sayısını bulur;
  tam tutar yoksa uygun alt ve üst alternatifleri sunar. Greedy'nin başarısız olduğu
  durum dahil, sekiz B3 kabul senaryosu test edilmiştir.
- **Veritabanı kilitleri:** Aynı banknotun iki çekime ayrılmasını ve farklı
  ATM'lerden aynı kartın günlük limitinin aşılmasını engellemek için ATM ve kart
  kilitleri kullanılır. Kilitler birden fazla API örneğinde de geçerlidir.
- **Kalıcı idempotency:** Çekim onayı ile tekrar isteğini tanıyan kayıt aynı
  transaction'da saklanır. Başarılı isteğin tekrarı aynı yanıtı döndürür ve nakdi
  ikinci kez düşmez.
- **Ledger ve outbox:** Stok değişikliği, değişmez hareket kaydı ve oluşan düşük
  nakit olayı aynı transaction'da yazılır. İşlem geri alındığında bu kayıtlar da
  geri alınır; bildirim gönderimi arka planda tekrar denenebilir.
- **Gerçek PostgreSQL testleri:** Kilit, transaction ve paralel istek davranışları
  kullanılan veritabanı üzerinde doğrulanır.

Algoritma örneği, kilit sırası, günlük limit, mutabakat ve outbox teslim garantisinin
ayrıntıları [teknik tasarım kararlarında](docs/design-decisions.md) yer alır.

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

[Uçtan uca Postman koleksiyonu](postman/atm-cash-flow.postman_collection.json)
tam teslim senaryosudur: oluştur → yapılandır → yükle → hizmete al → çekim teklifi
→ onay → yatırma → bakım → mutabakat → farklı kullanıcı onayı → tutarlılık kontrolü
→ rapor → hizmete dönüş.
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
`count` içeren `notes` dizisidir. Boş `notes` dizisi boş kaset demektir. Karışık
kaset bütün sayılan banknot türlerini içerir. Tam örnekler Swagger ve koleksiyondadır.

## Varsayımlar

- Kaset numaraları sabit: 1–4 recycling, 5 deposit-only; kapasiteleri 100 banknottur.
- İsimler benzersiz olmak zorunda değildir; ATM kimliği GUID'dir.
- İlk nakit yüklemesinde en az bir banknot gerekir; mutabakat başlangıç yüklemesini
  atlama yolu değildir. LOADED durumunda sonraki adım IN_SERVICE'dir.
- Aktif rezervasyon varken bakıma geçilmez. Banknot içeren kasetin kupürü/
  para birimi ancak boşaltıldıktan sonra değiştirilebilir.
- Kalan limiti aşan çekim isteği reddedilir. Sıfır alt alternatif sunulmaz.
- Kart girişi boşluksuz 13–19 ASCII rakamdır; Luhn kontrolü istenmemiştir.
- Banknot sayıları ve tutarlar tam sayıdır; TRY dönüşümü decimal kullanır. Başlangıç kurları
  TRY=1, USD=35, EUR=40 örnek değerlerdir, canlı piyasa kuru değildir. Kurlar
  ExchangeRates tablosunda tutulur; mutabakat geçmişi sonradan kur değişiminden etkilenmez.
- Yatırmada tekrarlanan türler birleştirilir; ortak deposit kapasitesinde grupların
  ilk görülme sırası kullanılır. 60 giriş grubu sınırı vardır; sığmayan büyük
  sayılar long toplamlarla döner. Yanıt seri numarası yerine kaset bazında sayılar verir.
- Tanınmayan ve iade edilen banknotlar stok veya ledger'a girmez. Yalnız tanınmayan banknot
  içeren istek kabul edilir, tamamen boş istek reddedilir.
- Confirm dışındaki POST'lar tekrar gönderilirse yeni işlemdir. İptal tekrarlanabilir.
- X-User trim edilir ve onayda harf duyarsız karşılaştırılır; kimlik doğrulama istenmemiştir.
- Mutabakat eşiği net fark yerine mutlak fark toplamıdır. Yüksek fark onaydan
  sonra uygulanır. Onaylanmış mutabakat tekrar onaylanamaz.

## Bitmemiş işler ve bilinen sınırlamalar

Vakadaki Part A ve B1–B10 geliştirmeleri tamamlandı; kapsam ve doğrulama
[gereksinim eşleştirmesinde](docs/implementation-plan.md#gereksinim-ve-doğrulama-eşleştirmesi)
yer alır. Üretim kapsamına ait kimlik doğrulama ve kart sahipliği kontrolü, gerçek
donanım ve müşteri hesap entegrasyonu, canlı kur servisi, anahtar rotasyonu ve ağ
raporunda sayfalama uygulanmadı. Bunlar vaka için talep edilmedi.

Outbox log teslimi en az bir kezdir; log yazımı ile `SentAt` commit'i arasındaki
çökmede aynı olay yeniden loglanabilir. Denetim ve ledger kayıtları silinmez;
saklama süresi ve arşivleme politikası eklenmedi. Yatırma yanıtı kabul hareketlerini
ilişkilendirir, ayrı yatırma geçmişi endpoint'i bulunmaz.

## Doğrulama

12 Ekim 2026: 82 birim ve 46 gerçek PostgreSQL entegrasyon testi geçti, atlanan test
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

OpenAI Codex gereksinim analizi, tasarım, uygulama, test ve dokümantasyon
geliştirmesinde kullanıldı. Üretilen uygulama derleme, birim testleri, gerçek
PostgreSQL entegrasyon testleri ve Docker üzerindeki uçtan uca akışla doğrulandı.
Eşzamanlılık, idempotency, zaman hassasiyeti ve işlem geri alma gibi kritik
davranışlar testlerle kontrol edildi.
