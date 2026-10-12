# Teknik tasarım kararları

Bu belge [README](../README.md)'de özetlenen kararların gerekçelerini ve işlem
davranışlarını açıklar. Gereksinim kapsamı ve geliştirme aşamaları
[uygulama planında](implementation-plan.md) yer alır.

## Katmanlar ve sorumluluklar

Domain ATM, kaset ve mutabakat kurallarını dış bağımlılıklardan bağımsız tutar.
Application kullanım senaryolarını koordine eder ve kalıcılık sözleşmelerini
tanımlar. Infrastructure bu sözleşmeleri PostgreSQL/EF Core ile uygular;
HMAC ve outbox gönderimi de bu katmandadır. API HTTP doğrulamasını, hata
yanıtlarını ve bağımlılık kayıtlarını yönetir.

Bu ayrım iş kurallarının HTTP veya veritabanı kurulmadan test edilmesini sağlar.
HTTP istek modelleri ve DTO'lar domain nesnelerini doğrudan değiştiremez.
Kalıcılık sözleşmeleri kullanım senaryolarına özeldir; transaction ve kilit
sınırları açık tutulur. Vaka için tek API ve PostgreSQL yeterli olduğundan
arka plan gönderimi aynı uygulamada çalışır.

## Banknot seçimi

Greedy yaklaşımı, sınırlı stokta ödenebilir bir tutarı kaçırabilir. Örneğin
stokta üç adet 50 ve on adet 20 varken 160 istenirse önce üç adet 50 seçmek
ödenemeyen 10 bırakır. İki adet 50 ve üç adet 20 ise 160'ı beş banknotla karşılar.

Bu nedenle sınırlı stokla knapsack/dinamik programlama kullanılır. Her erişilebilir
tutarda minimum banknot sayısı saklanır. Tutarlar 10 birimle ölçeklenir, stoklar
ikili paketlere ayrılır. Tam tutar yoksa en yakın pozitif alt ve kalan günlük
limite uyan üst tutar sunulur. Sıfır tutarlı alternatif üretilmez.

Greedy'nin başarısız olduğu durum dahil, sekiz B3 kabul senaryosu
[NoteSelectorTests](../tests/AtmCash.UnitTests/Withdrawals/NoteSelectorTests.cs)
içinde test edilir. Ek olarak küçük stokların bütün kombinasyonlarını deneyen
bağımsız bir test, minimum banknot ve stok sınırlarını kontrol eder.

## Kaset dağıtımı ve yatırma

Çekim teklifinde fiziksel doluluk oranı yüksek recycling kaseti önce kullanılır;
eşitlikte kaset numarası seçilir. Önceden rezerve edilmiş banknotlar seçilemez.
Teklifin kaset dağıtımı onay sırasında yeniden hesaplanmaz; böylece teklif anında
ayrılmış stok kullanılır.

Yatırmada eşleşen recycling kasetleri numara sırasıyla, ardından karışık deposit
kaseti doldurulur. Sığmayan banknotlar müşteriye iade edilir. Kapasite fiziksel
banknot sayısına bağlıdır; rezervasyon kasette yer açmaz. Tekrarlanan banknot
türleri birleştirilir; deposit kapasitesi için grupların ilk görülme sırası kullanılır.
Tanınmayan ve iade edilen banknotlar stok veya ledger'a eklenmez.

## Eşzamanlılık ve kilitler

ATM'yi değiştiren işlemler PostgreSQL transaction'ında ATM satırını
`SELECT ... FOR UPDATE` ile kilitler; envanter bundan sonra okunur. Böylece aynı
ATM'ye gelen işlemler birbirinin stok değişikliklerini görmeden banknot ayıramaz.
Bu tercih aynı ATM'deki mutasyonları sıraya alır.

Çekimlerde kullanılan kilitlerin sırası idempotency anahtarı → kart → ATM'dir.
İşlemler ihtiyaç duydukları kilitleri bu sırayla alır. Transaction kapsamlı kart
advisory kilidi, farklı ATM'lerde aynı kartın günlük limitini eşzamanlı aşmasını
engeller. Kilitler veritabanında olduğu için farklı API örneklerini de kapsar.
İptal ve süre dolumu ayrılan kullanımı azalttığından yalnız ATM kilidi alır.

İki API örneğine paralel istekler ve farklı ATM'lerde kart limiti
[WithdrawalTests](../tests/AtmCash.IntegrationTests/WithdrawalTests.cs)
içinde gerçek PostgreSQL üzerinde doğrulanır.

## Rezervasyon, günlük limit ve süre dolumu

Teklifler, alternatifler ve ayrılan kasetler veritabanında saklanır. İki alternatif
için kaset başına maksimum ihtiyaç rezerve edilir; günlük limitten seçeneklerin
en yüksek tutarı ayrılır. Alternatiflerin tutarları toplanmaz, çünkü müşteri yalnız
birini onaylar. TRY 20.000, USD/EUR 1.000 günlük limitler ayrı izlenir.

İş günü `Europe/Istanbul`, olay zamanları UTC'dir. Aynı İstanbul günündeki onaylar
ve henüz geçerli bütün teklifler limite dahil edilir. Gece yarısını geçen çekim
onaylandığı güne yazılır; teklifin önceki gün oluşturulmuş olması limit dışında
kalmasına yol açmaz.

Teklif 60 saniyede geçersizleşir. Arka plan servisi saniyelik taramayla rezervasyonu
bırakır; çekim endpoint'leri ilgili ATM'de temizlik de yapar. Stok temizliği sonraki
başarılı tarama veya işlemde yapılır; teklifin geçerliliği uzamaz. Süresi dolan
onay isteği rezervasyonun bırakılmasını commit eder, ardından HTTP 410 döner.

## Idempotency ve kart verisi

Başarılı çekim onayının anahtarı, teklif kimliği ve tutarı nakit düşümüyle aynı
transaction'da saklanır. Böylece istemci yanıtı alamayıp isteği tekrar gönderirse
nakit ikinci kez düşülmez. Sonuç kalıcı onaylanmış tekliften üretilir.

Aynı anahtar ve istek aynı JSON'u döndürür; farklı içerik HTTP 409 verir. Yeni
anahtarla aynı teklifi tekrar onaylamak da reddedilir. Doğrulama hataları anahtar
tüketmez. Zamanlar UTC ve mikrosaniye hassasiyetindedir; .NET/PostgreSQL zaman
hassasiyeti farkı ilk yanıt ile tekrar yanıtını değiştirmez.

Kartın günlük kullanımını izlemek için yalnız HMAC-SHA256 parmak izi saklanır.
Kart numarası ve parmak izi API yanıtına yazılmaz; istek gövdeleri ve hassas EF
parametreleri loglanmaz. HMAC anahtarının korunması, yeniden başlatma sonrasında
aynı kartın tanınması için gereklidir.

## Ledger ve mutabakat

LOAD, UNLOAD, WITHDRAWAL, DEPOSIT, RESERVATION, RESERVATION_RELEASE ve
RECONCILIATION_ADJUSTMENT hareketleri stokla aynı transaction'da kaydedilir.
Fiziksel ve rezervasyon değişimleri ayrıdır. EF koruması ve veritabanı trigger'ları
mevcut hareketlerde UPDATE, DELETE ve TRUNCATE'i reddeder. `ReferenceId` teklif,
yatırma veya mutabakatı ilişkilendirir. Çekim onayı tüm rezervasyonu bırakır ve
yalnız seçilen banknotları stoktan düşer.

Mutabakat beş kasetin tam sayımını içerir; bütün girdi değişiklik yapılmadan önce
doğrulanır. Farkların mutlak TRY değerleri toplanır: tam 1.000 otomatik uygulanır,
üzeri farklı `X-User` onayı bekler. Beklerken nakit, yapılandırma ve durum
değişiklikleri engellenir. Böylece onay, sayım sırasında kullanılan stok üzerinden
uygulanır.

Kur her sayım satırında saklanır; sonraki kur değişiklikleri geçmiş farkı değiştirmez.
Onay aynı ATM kilidinde bir kez uygulanır; sayım öncesi stokla mevcut stok
uyuşmuyorsa reddedilir. Fark yoksa denetim kaydı tutulur, sıfır değişimli hareket
oluşturulmaz.

## Raporlar ve tutarlılık kontrolü

Operasyon raporu stok ve kurları aynı `REPEATABLE READ` transaction'ında okur.
Tutarlılık kontrolü ise stok ve ledger toplamlarını kendi `REPEATABLE READ`
transaction'ında okur. Böylece her yanıtın içindeki okumalar aynı veritabanı
görünümünü kullanır; rapor ile tutarlılık kontrolü ayrı isteklerdir. ATM ve ağ
raporları para birimi toplamlarını ve TRY değerlerini içerir.

Tutarlılık kontrolü SQL'de bütün hareketlerin fiziksel ve rezervasyon değişimlerini
toplar; saklanan banknot ve rezerve miktarlarıyla karşılaştırır. Ledger'da bulunan
ama stokta eksik olan satırlar da raporlanır.

## Düşük nakit ve outbox

Kullanılabilir recycling stoğu 10'un altına indiğinde olay ve kasetin uyarı durumu
aynı nakit transaction'ında yazılır. Bu, nakit işlemi geri alınırken ona ait
bildirimin kalmasını engeller. İlk yüklemede düşük olan kaset de olay üretir;
stok 10'a döndüğünde yeni bir düşüş için uyarı tekrar kurulabilir.

Dispatcher `FOR UPDATE SKIP LOCKED` ile en fazla 100 olaylık grup alır,
yapılandırılmış loga yazar ve `SentAt` değerini commit eder. Birden fazla
dispatcher aynı bekleyen kayıtları birlikte işlemez. Hata halinde kayıtlar
gönderilmemiş kalır ve yeniden denenir.

Teslim garantisi en az bir kezdir: log yazımı ile `SentAt` commit'i arasındaki
çökmede aynı olay tekrar loglanabilir. Bu sınır [README](../README.md)'de de belirtilir.
Nakit ve outbox rollback'i, hata sonrası tekrar deneme, paralel dispatcher ve
arka plan gönderimi [OperationsTests](../tests/AtmCash.IntegrationTests/OperationsTests.cs)
içinde gerçek PostgreSQL üzerinde doğrulanır.

## Hata modeli

Yapısal doğrulama HTTP 400, bulunamayan kaynak 404, durum/onay çatışması 409,
süresi dolan teklif 410, iş kuralı ihlali 422 ve eksik kur 503 döner. Hatalar
`application/problem+json`, sabit `code` ve `traceId` içerir. Beklenmeyen hata
ayrıntıları istemciye verilmez.

## Migration yönetimi

EF Core migration'ları API başlangıcında otomatik uygulanır. Yeni model
değişiklikleri için çözümün kök klasöründe:

```sh
dotnet tool restore
dotnet ef migrations add ChangeName --project src/AtmCash.Infrastructure --startup-project src/AtmCash.Api --output-dir Persistence/Migrations
dotnet ef migrations has-pending-model-changes --project src/AtmCash.Infrastructure --startup-project src/AtmCash.Api
```
