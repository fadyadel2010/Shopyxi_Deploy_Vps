import { isRouteErrorResponse, Link, useRouteError } from "react-router-dom";
import { brand } from "@/shared/brand";
import { useLanguageStore } from "@/store/language-store";

export const RouteRecoveryPage = ({ notFound = false }: { notFound?: boolean }) => {
  const error = useRouteError();
  const ar = useLanguageStore((state) => state.language === "ar");
  const missing = notFound || (isRouteErrorResponse(error) && error.status === 404);
  // Do not turn authentication, authorization, or server errors into a fake 404.
  const status = isRouteErrorResponse(error) ? error.status : undefined;
  return <main className="container py-16" dir={ar ? "rtl" : "ltr"}>
    <p className="font-display text-xl font-bold">{brand.fullName}</p>
    <h1 className="mt-6 text-3xl font-bold">{missing ? (ar ? "الصفحة غير موجودة" : "Page not found") : (ar ? "تعذر عرض الصفحة" : "This page could not be displayed")}</h1>
    <p role="status" className="my-6">{missing ? (ar ? "تحقق من الرابط أو تابع تصفح المنتجات." : "Check the link or continue browsing products.") : `${ar ? "حدث خطأ. حاول مرة أخرى." : "An error occurred. Please try again."}${status ? ` (${status})` : ""}`}</p>
    <nav aria-label={ar ? "العودة للتصفح" : "Recovery navigation"} className="flex gap-6">
      <Link to="/">{ar ? "الرئيسية" : "Home"}</Link>
      <Link to="/products">{ar ? "المنتجات" : "Browse products"}</Link>
    </nav>
  </main>;
};
