import { ArrowRight, ChevronRight, History, PackageCheck, ReceiptText, RotateCcw, UserRound } from "lucide-react";
import { useQuery } from "@tanstack/react-query";
import { Link, useNavigate } from "react-router-dom";

import { customerOrdersQueryOptions } from "@/features/customer/queries";
import { ItemStatusBadge, OrderStatusBadge } from "@/features/customer/status";
import { customerHasNavigationCapability } from "@/auth/customer-session-authority";
import { EmptyState } from "@/shared/EmptyState";
import { InlineErrorState } from "@/shared/InlineErrorState";
import { SectionHeading } from "@/shared/SectionHeading";
import { selectCustomerProfile, useCustomerAuthStore } from "@/store";
import { Button } from "@/ui/button";
import { Card, CardContent } from "@/ui/card";
import { formatCurrency, useDocumentTitle } from "@/utils";
import { useCopy } from "@/i18n/useCopy";
import { useLanguageStore } from "@/store/language-store";

export const CustomerDashboardPage = () => {
  const t = useCopy();
  const language = useLanguageStore((state) => state.language);
  const locale = language === "ar" ? "ar-EG" : "en-EG";
  const number = (value: number) => new Intl.NumberFormat(locale).format(value);
  const date = (value: string) => new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
  const statusLabels: Record<string, string> = {
    Pending: "قيد الانتظار", "Awaiting Payment": "بانتظار الدفع", AwaitingPayment: "بانتظار الدفع",
    "Payment Pending": "الدفع قيد الانتظار", PaymentPending: "الدفع قيد الانتظار",
    Confirmed: "مؤكد", Processing: "قيد المعالجة", Packed: "تم التغليف",
    "Ready For Shipment": "جاهز للشحن", ReadyForShipment: "جاهز للشحن",
    Shipped: "تم الشحن", Delivered: "تم التسليم", Completed: "مكتمل", Cancelled: "ملغي",
    Returned: "مرتجع", Refunded: "مسترد", Failed: "فشل", OnHold: "معلق",
  };
  const navigate = useNavigate();
  const customer = useCustomerAuthStore(selectCustomerProfile);
  const canManageAccount = customerHasNavigationCapability(customer, "customer.account.settings");
  const canViewOrders = customerHasNavigationCapability(customer, "customer.orders.view");
  const canViewRefunds = customerHasNavigationCapability(customer, "customer.refunds.view");
  const recentOrdersQuery = useQuery({
    ...customerOrdersQueryOptions({ page: 1, pageSize: 3 }),
    enabled: canViewOrders
  });

  useDocumentTitle(t("Customer Account | CommerceHub", "حساب العميل | CommerceHub"));

  const hasOrders = canViewOrders && recentOrdersQuery.isSuccess && !recentOrdersQuery.isFetching
    && Array.isArray(recentOrdersQuery.data?.items)
    && Number.isFinite(recentOrdersQuery.data?.totalCount) && recentOrdersQuery.data.totalCount >= 0;
  const recentOrders = hasOrders ? recentOrdersQuery.data!.items : [];
  const inFlightSubOrders = recentOrders.reduce(
    (count, order) =>
      count + order.subOrders.filter((subOrder) => !["Delivered", "Completed", "Cancelled", "Refunded"].includes(subOrder.status)).length,
    0
  );
  const refundReadyItems = recentOrders.reduce(
    (count, order) =>
      count +
      order.subOrders.reduce((subOrderCount, subOrder) => subOrderCount + subOrder.items.filter((item) => item.canRefund).length, 0),
    0
  );

  return (
    <div className="space-y-8">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <SectionHeading
          eyebrow={t("Customer Space", "مساحة العميل")}
          title={customer ? `${t("Welcome back,", "مرحبًا بعودتك،")} ${customer.name.split(" ")[0]}` : t("Your customer account", "حسابك كعميل")}
          description={t("Track every Shopixy marketplace order with backend-issued vendor sub-orders owned by you in one secure place.", "تابع طلباتك من سوق شوبيكسي وطلبات التجار الفرعية الخاصة بك في مكان آمن واحد.")}
        />

        <div className="flex flex-wrap items-center gap-3">
          {canManageAccount ? (
            <Button asChild variant="outline" size="lg" className="gap-2">
              <Link to="/customer/settings">
                {t("Account settings", "إعدادات الحساب")}
                <UserRound className="h-4 w-4" />
              </Link>
            </Button>
          ) : null}
          {canViewRefunds ? (
            <Button asChild variant="outline" size="lg" className="gap-2">
              <Link to="/customer/refunds">
                {t("Refund requests", "طلبات الاسترداد")}
                <RotateCcw className="h-4 w-4" />
              </Link>
            </Button>
          ) : null}
          {canViewOrders ? (
            <Button asChild size="lg" className="gap-2">
              <Link to="/orders">
                {t("View all orders", "عرض كل الطلبات")}
                <ArrowRight className="h-4 w-4" />
              </Link>
            </Button>
          ) : null}
        </div>
      </div>

      <div className="grid gap-4 lg:grid-cols-[minmax(0,1.1fr)_repeat(3,minmax(0,0.65fr))]">
        <Card className="border-white/70 bg-white/95">
          <CardContent className="flex h-full flex-col justify-between gap-5 p-6">
            <div className="space-y-3">
              <div className="inline-flex w-fit items-center gap-2 rounded-full bg-primary/10 px-3 py-1 text-xs font-semibold text-primary">
                <UserRound className="h-4 w-4" />
                {t("Account profile", "الملف الشخصي")}
              </div>
              <div>
                <h2 className="text-2xl font-black tracking-tight text-foreground">{customer?.name ?? t("Customer", "العميل")}</h2>
                <p className="text-sm text-muted-foreground">{customer?.email}</p>
                {customer?.phoneNumber ? <p className="text-sm text-muted-foreground">{customer.phoneNumber}</p> : null}
              </div>
            </div>

            <p className="text-sm leading-6 text-muted-foreground">
              {t("Your account is isolated from vendor and admin systems, so only you can view these orders and your refund requests.", "حسابك منفصل عن أنظمة البائعين والإدارة لحماية طلباتك وطلبات الاسترداد الخاصة بك.")}
            </p>
          </CardContent>
        </Card>

        {canViewOrders ? <Card className="border-white/70 bg-white/90">
          <CardContent className="space-y-3 p-6">
            <p className="text-xs font-semibold uppercase tracking-[0.18em] text-muted-foreground">{t("Orders")}</p>
            <p className="text-4xl font-black tracking-tight text-foreground">
              {hasOrders ? number(recentOrdersQuery.data!.totalCount) : "—"}
            </p>
            <p className="text-sm text-muted-foreground">{t("Marketplace orders linked to your customer ID.", "طلبات السوق المرتبطة بحسابك.")}</p>
          </CardContent>
        </Card> : null}

        {canViewOrders ? <Card className="border-white/70 bg-white/90">
          <CardContent className="space-y-3 p-6">
            <p className="text-xs font-semibold uppercase tracking-[0.18em] text-muted-foreground">{t("In Flight", "قيد التنفيذ")}</p>
            <p className="text-4xl font-black tracking-tight text-foreground">{hasOrders ? number(inFlightSubOrders) : "—"}</p>
            <p className="text-sm text-muted-foreground">{t("Vendor sub-orders still in fulfillment among the three most recent orders.", "طلبات التجار الفرعية قيد التنفيذ ضمن أحدث ثلاثة طلبات.")}</p>
          </CardContent>
        </Card> : null}

        {canViewOrders && canViewRefunds ? <Card className="border-white/70 bg-white/90">
          <CardContent className="space-y-3 p-6">
            <p className="text-xs font-semibold uppercase tracking-[0.18em] text-muted-foreground">{t("Refund Ready", "مؤهل للاسترداد")}</p>
            <p className="text-4xl font-black tracking-tight text-foreground">{hasOrders ? number(refundReadyItems) : "—"}</p>
            <p className="text-sm text-muted-foreground">{t("Refund-eligible items among the three most recent orders.", "المنتجات المؤهلة للاسترداد ضمن أحدث ثلاثة طلبات.")}</p>
          </CardContent>
        </Card> : null}
      </div>

      {canViewOrders ? <Card className="border-white/70 bg-white/92">
        <CardContent className="space-y-5 p-6 md:p-7">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="space-y-1">
              <p className="text-xs font-semibold uppercase tracking-[0.18em] text-muted-foreground">{t("Recent Orders", "أحدث الطلبات")}</p>
              <h2 className="text-2xl font-black tracking-tight text-foreground">{t("Latest marketplace activity", "أحدث نشاط في السوق")}</h2>
            </div>

            <Button asChild variant="outline" size="sm" className="gap-2">
              <Link to="/orders">
                {t("Order history", "سجل الطلبات")}
                <ChevronRight className="h-4 w-4" />
              </Link>
            </Button>
          </div>

          {recentOrdersQuery.isLoading || recentOrdersQuery.isFetching ? (
            <div role="status" aria-label={t("Loading orders", "جارٍ تحميل الطلبات")} className="grid gap-4 md:grid-cols-3">
              {[0, 1, 2].map((item) => (
                <div key={item} className="rounded-[1.75rem] border border-border/70 bg-slate-50/80 p-5">
                  <div className="space-y-3">
                    <div className="h-3 w-20 rounded-full bg-slate-200" />
                    <div className="h-7 w-40 rounded-full bg-slate-200" />
                    <div className="h-4 w-full rounded-full bg-slate-200" />
                    <div className="h-4 w-3/4 rounded-full bg-slate-200" />
                  </div>
                </div>
              ))}
            </div>
          ) : !hasOrders ? (
            <InlineErrorState
              title={t("Orders unavailable", "الطلبات غير متاحة")}
              message={t("We could not load your recent Shopixy orders right now.", "تعذر تحميل أحدث طلباتك الآن.")}
              actionLabel={t("Try again", "إعادة المحاولة")}
              onRetry={() => void recentOrdersQuery.refetch()}
            />
          ) : recentOrders.length === 0 ? (
            <EmptyState
              icon={<History className="h-6 w-6" />}
              title={t("No customer orders yet", "لا توجد طلبات حتى الآن")}
              description={t("Once you place a Shopixy order, it will appear here with backend-issued vendor sub-orders and item status tracking.", "عند إتمام طلب، سيظهر هنا مع طلبات التجار الفرعية وحالة المنتجات.")}
              actionLabel={t("Browse products")}
              onAction={() => navigate("/products")}
            />
          ) : (
            <div className="grid gap-4 md:grid-cols-3">
              {recentOrders.map((order) => (
                <Card key={order.orderId} className="border-border/70 bg-slate-50/70 shadow-none">
                  <CardContent className="space-y-4 p-5">
                    <div className="flex items-start justify-between gap-3">
                      <div className="space-y-1">
                        <p className="text-xs font-semibold uppercase tracking-[0.18em] text-muted-foreground">{t("Order", "الطلب")}</p>
                        <h3 className="text-xl font-black tracking-tight text-foreground">{order.orderNumber}</h3>
                        <p className="text-sm text-muted-foreground">{date(order.createdAt)}</p>
                      </div>
                      <OrderStatusBadge status={order.status} label={t(order.status, statusLabels[order.status])} />
                    </div>

                    <div className="space-y-3 rounded-[1.5rem] border border-white/80 bg-white/90 p-4">
                      {order.subOrders.slice(0, 2).map((subOrder) => (
                        <div key={subOrder.subOrderId} className="space-y-2">
                          <div className="flex items-center justify-between gap-3">
                            <p className="text-sm font-semibold text-foreground">{subOrder.vendorName}</p>
                            <p className="text-sm font-semibold text-foreground">{formatCurrency(subOrder.totalAmount, order.currency, { locale })}</p>
                          </div>
                          {subOrder.items[0] ? (
                            <div className="flex items-center justify-between gap-3 text-xs text-muted-foreground">
                              <span className="truncate">{subOrder.items[0].productName}</span>
                              <ItemStatusBadge status={subOrder.items[0].status} label={t(subOrder.items[0].status, statusLabels[subOrder.items[0].status])} />
                            </div>
                          ) : null}
                        </div>
                      ))}
                    </div>

                    <div className="flex items-center justify-between gap-3">
                      <div className="space-y-1">
                        <p className="text-xs font-semibold uppercase tracking-[0.18em] text-muted-foreground">{t("Total", "الإجمالي")}</p>
                        <p className="text-2xl font-black tracking-tight text-foreground">{formatCurrency(order.totalAmount, order.currency, { locale })}</p>
                      </div>

                      <Button asChild variant="outline" size="sm" className="gap-2">
                        <Link to={`/orders/${order.orderId}`}>
                          {t("Details", "التفاصيل")}
                          <ChevronRight className="h-4 w-4" />
                        </Link>
                      </Button>
                    </div>
                  </CardContent>
                </Card>
              ))}
            </div>
          )}
        </CardContent>
      </Card> : null}

      <div className="grid gap-4 md:grid-cols-3">
        <Card className="border-white/70 bg-white/90">
          <CardContent className="space-y-3 p-5">
            <ReceiptText className="h-5 w-5 text-primary" />
            <h3 className="text-xl font-black tracking-tight text-foreground">{t("Order ownership", "ملكية الطلبات")}</h3>
            <p className="text-sm leading-6 text-muted-foreground">{t("Every Shopixy order is now bound to your customer ID and validated on every request.", "يرتبط كل طلب بحسابك ويُتحقق من صلاحية الوصول إليه عند كل طلب للبيانات.")}</p>
          </CardContent>
        </Card>

        <Card className="border-white/70 bg-white/90">
          <CardContent className="space-y-3 p-5">
            <PackageCheck className="h-5 w-5 text-primary" />
            <h3 className="text-xl font-black tracking-tight text-foreground">{t("Item-level visibility", "متابعة كل منتج")}</h3>
            <p className="text-sm leading-6 text-muted-foreground">{t("Vendor items show Pending, Shipped, Delivered, or Cancelled so you can see where each line stands.", "تابع حالة كل منتج: قيد الانتظار أو تم الشحن أو تم التسليم أو ملغي.")}</p>
          </CardContent>
        </Card>

        <Card className="border-white/70 bg-white/90">
          <CardContent className="space-y-3 p-5">
            <History className="h-5 w-5 text-primary" />
            <h3 className="text-xl font-black tracking-tight text-foreground">{t("Refund eligibility", "أهلية الاسترداد")}</h3>
            <p className="text-sm leading-6 text-muted-foreground">{t("Refund eligibility is supplied by the service and rechecked before each request.", "تحدد الخدمة أهلية الاسترداد وتعيد التحقق منها قبل كل طلب.")}</p>
          </CardContent>
        </Card>
      </div>
    </div>
  );
};
