import React from 'react';
import { ArrowRight, Sparkles } from 'lucide-react';
import { labels, Mixed, products, useMarketing } from './core.jsx';
import { ProductGlyph } from './homepage.jsx';
import { CaptureImage, ScreenshotDisclosure } from './product-screen.jsx';
import { pagePath } from '../routes.mjs';
import screenshots from '../content/product-screenshots.json';

/**
 * Phase 1B Product Showcase. Four genuine application captures presented in
 * external browser-style chrome; nothing is ever drawn over screenshot pixels.
 * Copy and assets are locale-specific and driven by the audited manifests.
 */

const CARD = {
  pos: { accent: 'blue', desc: 'showpos', action: 'explorepos' },
  storefront: { accent: 'violet', desc: 'showstorefront', action: 'explorestorefront' },
  affiliate: { accent: 'green', desc: 'showaffiliate', action: 'exploreaffiliate' },
  commercehub: { accent: 'amber', desc: 'showcommercehub', action: 'explorecommercehub' }
};

const PRODUCT_BY_KEY = Object.fromEntries(products.map(product => [product.key, product]));

/**
 * Genuine desktop capture selection:
 * - POS, Storefront, Affiliate and CommerceHub all use their full desktop
 *   capture rather than a folio crop. The POS folio crop loses the product-name
 *   column (and clips the Arabic column header), so it does not meet the visual
 *   and information bar for the showcase.
 */
function ShowcaseCard({ product }) {
  const { locale, t } = useMarketing();
  const item = PRODUCT_BY_KEY[product];
  const config = CARD[product];
  const screen = screenshots.products[product][locale];
  const image = screen.desktop;
  if (!image) return null;
  const descriptionId = `showcase-description-${product}`;
  const brand = labels.en[product];
  return <li className={`ps-item ps-accent-${config.accent}`}>
    <article className="ps-card">
      <div className="ps-card-body">
        <div className="ps-copy">
          <div className="ps-meta">
            <bdi className="ps-number" dir="ltr">{item.n}</bdi>
            <span className="ps-audience"><Mixed text={t(item.audience)} /></span>
          </div>
          <div className="ps-heading">
            <span className="ps-icon" aria-hidden="true"><ProductGlyph product={product} size={22} /></span>
            <div className="ps-title">
              <h3><a className="ps-card-link" href={`${pagePath(locale, product)}#main`} aria-describedby={descriptionId}>
                <span className="ps-card-link-label"><Mixed text={t(product)} /></span>
              </a></h3>
              {locale === 'ar' && <span className="ps-brand" aria-hidden="true">{brand}</span>}
            </div>
          </div>
          <p className="ps-desc" id={descriptionId}>{t(config.desc)}</p>
          <span className="ps-action" aria-hidden="true">
            <span><Mixed text={t(config.action)} /></span><ArrowRight className="ps-action-arrow" strokeWidth={2} />
          </span>
        </div>
        <figure className="ps-media" data-screen-product={product} data-screen-candidate={image.candidate} data-screen-variant="desktop">
          <span className="ps-glow" aria-hidden="true" />
          <div className="ps-window">
            <div className="ps-window-bar" aria-hidden="true">
              <span className="ps-lights"><i /><i /><i /></span>
              <span className="ps-window-label"><Mixed text={t(product)} /></span>
            </div>
            <span className="ps-shot"><CaptureImage image={image} alt={screen.alt} /></span>
          </div>
        </figure>
      </div>
    </article>
  </li>;
}

export function ProductShowcase() {
  const { t } = useMarketing();
  return <section className="ps-section" id="products" tabIndex="-1" aria-labelledby="product-showcase-title">
    <div className="ps-shell">
      <span className="ps-corner ps-corner-start" aria-hidden="true" />
      <span className="ps-corner ps-corner-end" aria-hidden="true" />
      <header className="ps-intro">
        <span className="ps-eyebrow"><Sparkles size={14} strokeWidth={2} aria-hidden="true" /><Mixed text={t('showcaseeyebrow')} /></span>
        <h2 id="product-showcase-title">{t('showcaseheading')}</h2>
        <p className="ps-sub" data-claim-fragment="C01">{t('showcasesub')}</p>
      </header>
      <div className="ps-grid-shell">
        <ul className="ps-grid">
          {products.map(product => <ShowcaseCard key={product.key} product={product.key} />)}
        </ul>
      </div>
      <ScreenshotDisclosure />
    </div>
  </section>;
}
