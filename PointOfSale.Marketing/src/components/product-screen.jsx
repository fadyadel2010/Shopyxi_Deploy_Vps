import React from 'react';
import { useMarketing } from './core.jsx';
import screenshots from '../content/product-screenshots.json';

export function ScreenshotDisclosure({ desktopNote = false }) {
  const { locale } = useMarketing();
  return <div className="screenshot-disclosure"><p className="screen-disclosure">{screenshots.disclosure[locale]}</p>
    {desktopNote && <p className="screen-note">{locale === 'ar' ? 'تفاصيل من تطبيقات سطح المكتب.' : 'Desktop application details.'}</p>}</div>;
}

/** Single audited image renderer shared by product pages and the composition layer. */
export function CaptureImage({ image, alt, priority = false }) {
  return <img src={image.publicPath} alt={alt}
    width={image.dimensions.width} height={image.dimensions.height}
    loading={priority ? 'eager' : 'lazy'} fetchpriority={priority ? 'high' : undefined} decoding="async" />;
}

/** Every product image uses the same audited, lossless capture manifest. */
export function ProductScreenshot({
  product, priority = false, compact = false, folio = false, disclosure = true,
  variant = null, figureClass = '', frameClass = '', caption = null, accessibleLabel = null,
  windowRole = null
}) {
  const { locale, t } = useMarketing();
  const screen = screenshots.products[product][locale];
  const image = variant ? screen[variant] : folio || compact ? screen.folio : screen.desktop;
  if (!image) return null;
  const composed = Boolean(variant);
  const label = locale === 'ar' ? 'عرض لقطة التطبيق بحجمها الأصلي' : 'View application capture at original size';
  return <figure className={composed ? figureClass : `product-screenshot${compact ? ' screenshot-compact' : ''}${folio ? ' screenshot-folio' : ''}`}
    data-screen-product={product} data-screen-candidate={image.candidate} data-screen-variant={variant || undefined}
    data-window-role={windowRole || undefined}>
    <a className={composed ? frameClass : `screenshot-frame${!folio && !compact && screen.mobile ? ' has-mobile' : ''}`}
      href={composed ? image.publicPath : screen.desktop.publicPath} aria-label={accessibleLabel || `${label} — ${t(product)}`}>
      <picture>
        {!composed && !folio && !compact && screen.mobile && <source media="(max-width: 640px)" srcSet={screen.mobile.publicPath}
          width={screen.mobile.dimensions.width} height={screen.mobile.dimensions.height} />}
        <CaptureImage image={image} alt={variant === 'folio' || folio || compact ? screen.detailAlt : screen.alt} priority={priority} />
      </picture>
    </a>
    {composed ? caption : !folio && (!compact || disclosure) && <figcaption>
      {disclosure && <span className="screen-disclosure">{screenshots.disclosure[locale]}</span>}
      {(screen.desktopOnly || compact) && <span className="screen-note">{locale === 'ar' ? 'عرض تطبيق سطح المكتب.' : 'Desktop application view.'}</span>}
      {!compact && <a className="screen-full-link" href={screen.desktop.publicPath}>{label}</a>}
    </figcaption>}
  </figure>;
}
