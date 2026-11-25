import React from 'react';
import { Label, Mixed, products, useMarketing } from './core.jsx';
import { ProductScreenshot, ScreenshotDisclosure } from './product-screen.jsx';
import screenshots from '../content/product-screenshots.json';
import copy from '../content/composition.json';

const productByKey = Object.fromEntries(products.map(product => [product.key, product]));

/**
 * Renders one independent screenshot window and its external product caption.
 * Layout is owned by the surrounding composition classes, never by this component.
 */
export function FloatingProductScreenshot({
  product, variant = 'desktop', role = 'support', index = true, note = null, noteDetail = null,
  priority = false, className = '', phone = false, compact = false
}) {
  const { locale, t } = useMarketing();
  if (!screenshots.products[product][locale][variant]) return null;
  const item = productByKey[product];
  const detail = role === 'detail';
  const name = t(product);
  const caption = detail ? copy[locale][note] : name;
  const accessible = `${copy[locale].largeView} — ${name}${detail ? ` — ${copy[locale][note]}` : ''}`;
  return <ProductScreenshot product={product} variant={variant} priority={priority} disclosure={false}
    figureClass={`screenshot-window composition-item item-${product} item-${role}${phone ? ' item-phone' : ''}${className ? ` ${className}` : ''}`}
    frameClass="window-frame" accessibleLabel={accessible} windowRole={role}
    caption={<figcaption className={`window-caption${compact ? ' window-caption-compact' : ''}`}>
      {index && !detail && <bdi className="window-index" dir="ltr">{item.n}</bdi>}
      <span className="window-identity">
        <strong className="window-name">{detail ? caption : <Mixed text={caption} />}</strong>
        {!compact && <span className="window-role">{detail ? <Mixed text={name} /> : <Label id={item.audience} claim="P2C01" />}</span>}
        {detail && noteDetail && <span className="window-note">{copy[locale][noteDetail]}</span>}
      </span>
    </figcaption>} />;
}

function CompositionCaption({ end = '01—04' }) {
  const { t } = useMarketing();
  return <div className="composition-rail"><span>{t('maplabel')}</span><bdi dir="ltr">{end}</bdi></div>;
}

function CompositionDisclosure({ desktopNote = false }) {
  const { locale } = useMarketing();
  return <><p className="composition-inspection-hint">{copy[locale].inspectionHint}</p>
    <ScreenshotDisclosure desktopNote={desktopNote} /></>;
}

/** Homepage: one dominant operational surface with three supporting product windows. */
export function ProductHeroComposition() {
  const { locale } = useMarketing();
  return <aside className="portfolio hero-composition" data-composition="hero" aria-label={copy[locale].compositionLabel}>
    <CompositionCaption />
    <div className="composition-stage hero-stage">
      <FloatingProductScreenshot product="pos" role="primary" priority className="hero-pos" />
      <FloatingProductScreenshot product="storefront" variant="folio" role="support" compact className="hero-storefront" />
      <FloatingProductScreenshot product="affiliate" variant="folio" role="support" compact className="hero-affiliate" />
      <FloatingProductScreenshot product="commercehub" variant="folio" role="support" compact className="hero-commercehub" />
    </div>
    <CompositionDisclosure desktopNote />
  </aside>;
}

/** Platform: the four working surfaces arranged as one ecosystem around the operational core. */
export function PlatformEcosystemComposition() {
  const { locale } = useMarketing();
  return <div className="platform-composition" data-composition="platform" role="group" aria-label={copy[locale].compositionLabel}>
    <CompositionCaption end="04 / 04" />
    <div className="composition-stage platform-stage">
      <FloatingProductScreenshot product="pos" role="primary" priority className="platform-pos" />
      <FloatingProductScreenshot product="storefront" variant="folio" role="support" compact className="platform-storefront" />
      <FloatingProductScreenshot product="affiliate" variant="folio" role="support" compact className="platform-affiliate" />
      <FloatingProductScreenshot product="commercehub" variant="folio" role="support" compact className="platform-commercehub" />
    </div>
    <CompositionDisclosure desktopNote />
  </div>;
}

const showcases = {
  pos: [
    { product: 'pos', variant: 'desktop', role: 'primary', className: 'showcase-primary' },
    { product: 'pos', variant: 'folio', role: 'detail', note: 'inventoryDetail', noteDetail: 'inventoryDetailNote', className: 'showcase-detail' }
  ],
  storefront: [
    { product: 'storefront', variant: 'desktop', role: 'primary', className: 'showcase-primary' },
    { product: 'storefront', variant: 'folio', role: 'detail', note: 'catalogueDetail', className: 'showcase-detail' },
    { product: 'storefront', variant: 'mobile', role: 'detail', note: 'mobileStorefront', className: 'showcase-phone', phone: true }
  ],
  affiliate: [
    { product: 'affiliate', variant: 'desktop', role: 'primary', className: 'showcase-primary' },
    { product: 'affiliate', variant: 'folio', role: 'detail', note: 'discoveryDetail', className: 'showcase-detail' },
    { product: 'affiliate', variant: 'mobile', role: 'detail', note: 'mobileAffiliate', className: 'showcase-phone', phone: true }
  ],
  commercehub: [
    { product: 'commercehub', variant: 'desktop', role: 'primary', className: 'showcase-primary' },
    { product: 'commercehub', variant: 'folio', role: 'detail', note: 'commissionPreview', className: 'showcase-detail' }
  ]
};

/** Product study: a product-specific arrangement around the dominant genuine capture. */
export function ProductShowcaseComposition({ product, priority = false }) {
  const { locale, t } = useMarketing();
  const desktopOnly = ['pos', 'commercehub'].includes(product);
  return <div className={`product-showcase showcase-${product}`} data-composition="showcase" data-showcase-product={product}
    role="group" aria-label={`${t(product)} — ${copy[locale].galleryLabel}`}>
    <div className="showcase-rail"><span>{copy[locale].galleryLabel}</span><bdi dir="ltr">{productByKey[product].n} / 04</bdi></div>
    <div className="composition-stage showcase-stage">
      {showcases[product].map((item, i) => <FloatingProductScreenshot key={`${item.product}-${item.variant}`}
        {...item} index={false} priority={priority && i === 0} />)}
    </div>
    <CompositionDisclosure desktopNote={desktopOnly} />
  </div>;
}
