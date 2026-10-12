# Uygulama planı

Kaynak: `Project_Assignment_Software_Developer.pdf`. Bu dosya vaka gereksinimlerini
izlemek içindir; kullanıcı talimatlarının yerine geçmez.

## Aşamalar

1. **Altyapı ve ilk dikey akış:** .NET çözümü, PostgreSQL, EF migration,
   Docker Compose, OpenAPI, ProblemDetails, health; ATM oluşturma,
   konfigürasyon, yükleme, hizmet/bakım geçişleri. B3 algoritması ve sekiz test.
2. **Para çekme:** quote/confirm/cancel, 60 saniye rezervasyon, idempotency,
   kart HMAC, para birimi bazında günlük limit, ATM/kart eşzamanlılığı.
3. **Operasyonlar:** yatırma ve taşma, ledger tutarlılık kontrolü,
   mutabakat ve farklı kullanıcı onayı, kur tablosu, operasyon raporu.
4. **Arka plan ve teslim:** düşük nakit/outbox, tam uçtan uca Postman,
   teslim dokümantasyonu. Rezervasyon temizleme ve çekim eşzamanlılık testleri
   ikinci aşamayla birlikte tamamlandı.

Her aşama derleme ve ilgili testlerle doğrulanır. Tamamlanmamış özellikler
README'de açıkça belirtilir; tasarım niyeti çalışan özellik gibi sunulmaz.

## Güncel durum — 12 Ekim 2026

İlk iki aşama tamamlandı. Doğrulama: 53 unit test (B3'teki sekiz kabul
testi dahil) ve 24 gerçek PostgreSQL entegrasyon testi. İkinci aşama B4–B6,
kaset doluluk sırası, kalıcı idempotency, iptal, otomatik süre dolumu ve İstanbul
gece yarısı senaryolarını kapsar. İkinci migration Docker başlangıcında otomatik
uygulandı. Container üzerinde 12 adımlı setup/quote/confirm/replay/cancel akışı,
Swagger ve health doğrulandı. Onay tekrarı aynı JSON yanıtını döndürdü ve nakit
ikinci kez düşmedi. .NET/PostgreSQL zaman hassasiyeti farkı regresyon testiyle
yakalanıp düzeltildi. Derleme sıfır uyarı ve sıfır hatayla tamamlandı.
Sonraki geliştirme aşaması müşteri yatırması, mutabakat/onay ve raporlamadır.

## Gereksinim ve doğrulama eşleştirmesi

| Gereksinim | Doğrulama |
| --- | --- |
| A1–A7, B1–B2 | Para/kaset/kapasite ve yaşam döngüsü domain testleri; gerçek DB setup akışı |
| A8, B9 | Mutabakat farkları, TRY eşik ve farklı kullanıcı onayı testleri |
| A9–A10, B3 | Belgedeki sekiz otomatik banknot seçimi testi; limit/sınır testleri |
| A11, B7 | Eşleşen kaset, taşma, kapasite ve tanınmayan banknot testleri |
| A12, B10 | Kaset/para birimi raporu; düşük nakit outbox transaction testi |
| B4–B5 | Süre dolumu, iptal, tekrar confirm, limit ve HMAC testleri |
| B6 | Gerçek PostgreSQL üzerinde paralel quote/confirm testleri |
| B8 | Ledger replay ve tutarsızlık kontrolü; değişmezlik testi |
| Teknik teslim | Compose, migration, health, OpenAPI, ProblemDetails ve Postman senaryosu |

## Önceden belirlenen varsayımlar

- İş günü `Europe/Istanbul`; olay zamanları UTC.
- Günlük limit onay gününe yazılır. Tüm henüz geçerli teklifler oluşturuldukları
  günden bağımsız olarak güncel limitten düşer; gece yarısı çift kullanım önlenir.
- Sayımlar ve tutarlar tam sayıdır. Kur dönüşümü `decimal` kullanır.
- İki quote alternatifi için kaset bazında en yüksek ihtiyaç rezerve edilir.
  Günlük limitten seçeneklerin en yüksek tutarı ayrılır, tutarlar toplanmaz.
- Alt alternatif sıfır olamaz. Üst alternatif kalan günlük limite sığmalıdır.
- Aynı kupürlü kasetlerde çekim doluluk oranı yüksek olandan başlar;
  eşitlikte kaset numarası kullanılır. Rezervasyonlar kullanılabilir stoğu azaltır.
- Doluluk sırası quote anında uygulanır. Banknotlar ayrıldıktan sonra kaset
  dağıtımı onay sırasında yeniden hesaplanmaz.
- Kalan günlük limiti aşan istek reddedilir. Confirm, sunulan tutarlardan birini
  ve global kapsamlı 1–128 karakterlik Idempotency-Key değerini gerektirir.
- Başarılı confirm anahtarı kalıcıdır; doğrulama hataları anahtarı tüketmez.
- Teklif 60 saniyede geçersiz olur; stok temizliği hosted service'in sonraki
  başarılı taramasında veya aynı ATM'nin çekim işleminde yapılır.
- Aktif rezervasyon varken bakıma geçiş reddedilir.
- Mutabakat eşiği, farkların mutlak TRY değerlerinin toplamıdır.
  TRY 1.000 üzerindeki düzeltme onaydan sonra uygulanır; tam 1.000 onay istemez.
- Karışık deposit kasetinde sayım para birimi/kupür bazında yapılır.
- Banknot içeren recycling kasetinin para birimi/kupürü değiştirilemez;
  önce boşaltılmalıdır. İlk yüklemede en az bir banknot gereklidir.
- Başlangıçta kasetler UNCONFIGURED/CONFIGURED; sonradan MAINTENANCE
  durumunda değiştirilebilir. LOADED durumunda sonraki adım IN_SERVICE'dir.
- Kullanıcı kimliği gerektiğinde X-User kullanılır; vaka kimlik doğrulama istemez.
- Kart numarası kalıcı depoya veya loglara yazılmaz; gizli anahtarlı HMAC kullanılır.
- Kur seed değerleri örnek sabit verilerdir, canlı piyasa kuru değildir.
