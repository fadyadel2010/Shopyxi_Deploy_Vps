import { useLanguageStore } from "@/store/language-store";

// Customer-facing UI copy only. Published CMS/catalog text remains server-owned.
const arabic: Record<string, string> = {
  "Home": "الرئيسية", "Products": "المنتجات", "Cart": "السلة", "Orders": "الطلبات",
  "My Orders": "طلباتي", "Refunds": "المرتجعات", "Account": "الحساب", "Customer account": "حساب العميل",
  "Sign in": "تسجيل الدخول", "Sign In": "تسجيل الدخول", "Sign out": "تسجيل الخروج", "Create account": "إنشاء حساب",
  "Search Shopixy products, brands, and categories": "ابحث عن المنتجات والعلامات التجارية والفئات",
  "Discover products from independent marketplace merchants.": "اكتشف منتجات تجار السوق المستقلين.",
  "Discovery": "تصفح", "Explore products": "استكشف المنتجات", "Browse products": "تصفح المنتجات",
  "Browse live marketplace listings with backend-driven filters, dynamic facets, and seamless infinite scrolling.": "تصفح منتجات السوق واستخدم الفلاتر للعثور على ما يناسبك.",
  "Dynamic Shopixy search keeps products, facets, and empty-state recovery in sync with the current URL.": "ابحث عن المنتجات وعدّل الفلاتر للعثور على النتائج المناسبة.",
  "Show filters": "عرض الفلاتر", "Filters": "الفلاتر", "Close filters": "إغلاق الفلاتر", "Apply filters": "تطبيق الفلاتر",
  "Clear all": "مسح الكل", "Clear search and filters": "مسح البحث والفلاتر", "Clear filters": "مسح الفلاتر",
  "In stock": "متوفر", "Available": "متاح", "Out of stock": "غير متوفر", "View": "عرض",
  "No image available": "لا توجد صورة متاحة", "No gallery is available for this listing.": "لا يوجد معرض صور متاح لهذا المنتج.",
  "No products found": "لم يتم العثور على منتجات", "Products unavailable": "المنتجات غير متاحة",
  "No products match your search. Try another term or clear your search.": "لا توجد منتجات تطابق بحثك. جرّب عبارة أخرى أو امسح البحث.",
  "These filters currently narrow the results too much. Try removing one or more to widen the catalog.": "لا توجد نتائج بهذه الفلاتر. جرّب إزالة بعض الفلاتر.",
  "There are no published Shopixy products available right now.": "لا توجد منتجات منشورة متاحة حاليًا.",
  "Welcome to Shopixy": "مرحبًا بك في شوبيكسي", "Explore products from our marketplace merchants.": "استكشف منتجات تجار السوق.",
  "Homepage unavailable": "الصفحة الرئيسية غير متاحة", "Email": "البريد الإلكتروني", "Password": "كلمة المرور",
  "Enter your password": "أدخل كلمة المرور", "Forgot password?": "نسيت كلمة المرور؟",
  "Continue your customer account": "تابع إلى حسابك", "Use your email and password to open your orders dashboard and checkout securely.": "استخدم بريدك الإلكتروني وكلمة المرور لعرض طلباتك وإتمام الشراء بأمان.",
  "Your account helps protect your orders. Sign in to access only your own purchases and checkout securely.": "يساعد حسابك في حماية طلباتك. سجّل الدخول للوصول إلى مشترياتك فقط وإتمام الشراء بأمان.",
  "Keep every Shopixy order in one secure place.": "احتفظ بجميع طلباتك في مكان آمن واحد.",
  "Sign in once to follow multi-vendor orders, check item delivery status, and submit refund requests for delivered items.": "سجّل الدخول لمتابعة طلبات التجار وحالة التوصيل وطلبات الاسترداد.",
  "Track marketplace orders across all vendors in one place.": "تابع طلبات السوق من جميع التجار في مكان واحد.",
  "Review item-level delivery progress as stores ship independently.": "تابع تقدم توصيل كل منتج من المتاجر المختلفة.",
  "Request item-level refunds and track their status from one customer account.": "اطلب استرداد قيمة المنتجات وتابع حالتها من حساب واحد.",
};

export const useCopy = () => {
  const ar = useLanguageStore((state) => state.language === "ar");
  return (english: string, translation?: string) => ar ? translation ?? arabic[english] ?? english : english;
};
