import React from 'react';
import { useMarketing } from './core.jsx';

const descriptions = {
  en: {
    pos: 'Editorial retail scene: ceramics, linen and packed merchandise arranged around a blue shop counter.',
    storefront: 'Editorial product presentation: a ceramic pitcher, blue cup, linen and parcel on stone display plinths.',
    affiliate: 'Editorial promotion still life: product photographs, merchandise, blank tags and sculptural blue links.',
    commercehub: 'Editorial marketplace scene: three separate curated merchandise collections in blue-framed display bays.'
  },
  ar: {
    pos: 'مشهد تجاري توضيحي: أوانٍ خزفية ومنسوجات وبضائع معبّأة حول منضدة متجر زرقاء.',
    storefront: 'عرض توضيحي للمنتجات: إبريق خزفي وكوب أزرق ومنسوجات وطرد على منصّات حجرية.',
    affiliate: 'تكوين توضيحي للترويج: صور منتجات وبضائع وبطاقات فارغة وحلقات زرقاء مترابطة.',
    commercehub: 'مشهد توضيحي للسوق: ثلاث مجموعات مستقلة من المنتجات المنتقاة في مساحات عرض زرقاء.'
  }
};
export function EditorialImage({ product, priority = false, decorative = false, sizes = '(max-width: 640px) 100vw, (max-width: 1000px) 80vw, 50vw' }) {
  const { locale } = useMarketing();
  return <img className={`editorial-image art-${product}`} src={`/assets/editorial-${product}-960.webp`}
    srcSet={[480, 960, 1440].map(w => `/assets/editorial-${product}-${w}.webp ${w}w`).join(', ')}
    sizes={sizes} width="1536" height="1024" alt={decorative ? '' : descriptions[locale][product]}
    loading={priority ? 'eager' : 'lazy'} fetchpriority={priority ? 'high' : undefined} decoding="async" />;
}
export function EditorialNote() {
  const { locale } = useMarketing();
  return <span className="editorial-note">{locale === 'ar'
    ? 'عمل بصري توضيحي أصلي — ليس شاشة من التطبيق.'
    : 'Original editorial artwork — not an application screen.'}</span>;
}
export const capabilityGroups = {
  en: {
    pos: ['Products & variants', 'Warehouse stock', 'Purchasing & suppliers', 'Permission-based reporting'],
    storefront: ['Customer ordering', 'Merchant presentation', 'Merchant order operations'],
    affiliate: ['Eligible products & links', 'Commission stages', 'History & withdrawal requests'],
    commercehub: ['Seller eligibility', 'Marketplace publishing', 'Channel pricing & checks']
  },
  ar: {
    pos: ['المنتجات وخياراتها', 'مخزون المستودعات', 'المشتريات والموردون', 'التقارير وفق الصلاحيات'],
    storefront: ['طلبات العملاء', 'عرض المتجر للتاجر', 'إدارة الطلبات للتاجر'],
    affiliate: ['المنتجات المؤهلة والروابط', 'مراحل العمولة', 'السجل وطلبات السحب'],
    commercehub: ['أهلية البائع', 'النشر في السوق', 'تسعير القناة وفحوصها']
  }
};
