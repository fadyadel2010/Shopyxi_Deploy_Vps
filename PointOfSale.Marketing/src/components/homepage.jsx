import React from 'react';
import {
  ArrowRight, Boxes, Percent, ReceiptText, ShoppingBag, ShoppingCart, Store, Users
} from 'lucide-react';
import { Label, Mixed, PageLink, products, useMarketing } from './core.jsx';
import { CaptureImage } from './product-screen.jsx';
import screenshots from '../content/product-screenshots.json';
import copy from '../content/composition.json';
import { pagePath } from '../routes.mjs';

/**
 * Phase 1A header + hero presentation. Every image below comes from the audited
 * capture manifest through the shared CaptureImage renderer; the browser-style
 * frame is external chrome only and never covers screenshot pixels.
 */

// Approved Latin product brand names. The localized product names stay in
// labels.json (`pos`, `storefront`, ...) and are used for accessible names.
const BRAND = {
  pos: 'POS Admin',
  storefront: 'Storefront',
  affiliate: 'Affiliate System',
  commercehub: 'CommerceHub'
};

const PRODUCT_ICON = {
  pos: ReceiptText,
  storefront: Store,
  affiliate: Users,
  commercehub: ShoppingBag
};

// Value points reuse approved claim wording fragments only. Each item names the
// claim it is quoted from and renders nothing that the claim does not state.
const VALUE_ICON = [Boxes, ShoppingCart, Percent];
const VALUE_CLAIM = ['C07', 'C17', 'C23'];

/** Header mega-menu and hero tiles share the same verified product routes. */
export function productHref(locale, key) {
  return `${pagePath(locale, key)}#main`;
}

export function ProductGlyph({ product, size = 22 }) {
  const Icon = PRODUCT_ICON[product];
  return <Icon aria-hidden="true" size={size} strokeWidth={1.9} />;
}

/**
 * One genuine application window: browser-style chrome above the capture,
 * an unmodified <img> from the audited manifest, and a link to the original
 * capture. POS is the only priority image.
 */
function HeroWindow({ product, priority = false }) {
  const { locale, t } = useMarketing();
  const screen = screenshots.products[product][locale];
  const image = screen.desktop;
  if (!image) return null;
  return <figure className={`hero-window hero-window-${product}`} data-screen-product={product}>
    <a className="hero-window-frame" href={image.publicPath}>
      <span className="hero-window-bar">
        <span className="hero-window-lights" aria-hidden="true"><i /><i /><i /></span>
        <bdi className="window-index" dir="ltr" aria-hidden="true">{products.find(p => p.key === product).n}</bdi>
        <span className="window-name">{BRAND[product]}</span>
      </span>
      <span className="hero-window-shot">
        <CaptureImage image={image} alt={screen.alt} priority={priority} />
      </span>
      <span className="hx-sr-only">{copy[locale].largeView}</span>
    </a>
  </figure>;
}

function HeroWindows() {
  const { locale } = useMarketing();
  return <div className="hero-visual" role="group" aria-label={copy[locale].compositionLabel}>
    <div className="hero-windows">
      <span className="hero-stage-light" aria-hidden="true" />
      <div className="hero-window-layer hero-window-layer-rear"><HeroWindow product="commercehub" /></div>
      <div className="hero-window-layer hero-window-layer-middle"><HeroWindow product="affiliate" /></div>
      <div className="hero-window-layer hero-window-layer-front"><HeroWindow product="pos" priority /></div>
    </div>
  </div>;
}

function HeroActions() {
  const { t } = useMarketing();
  return <div className="actions hero-actions">
    <a className="button hero-cta-primary" href="#products" data-claim-fragment="C40">
      <span>{t('exploreproducts')}</span><ArrowRight className="hero-cta-arrow" aria-hidden="true" strokeWidth={2} />
    </a>
    <PageLink page="platform" className="button secondary hero-cta-secondary">{t('platformstudy')}</PageLink>
  </div>;
}

function HeroValuePoints() {
  const { t } = useMarketing();
  return <ul className="hero-points">
    {t('herovalues').map((text, i) => {
      const Icon = VALUE_ICON[i];
      return <li key={text} data-claim-fragment={VALUE_CLAIM[i]}>
        <Icon aria-hidden="true" size={20} strokeWidth={1.7} />
        <span><Mixed text={text} /></span>
      </li>;
    })}
  </ul>;
}

function HeroProductNav() {
  const { t, locale } = useMarketing();
  return <nav className="hero-products" aria-label={t('productnav')}>
    <ul className="product-nav">{products.map(p => <li key={p.key}>
      <a className="product-tile" href={productHref(locale, p.key)}>
        <span className={`product-tile-icon tile-icon-${p.key}`} aria-hidden="true"><ProductGlyph product={p.key} /></span>
        <span className="product-tile-body">
          <bdi className="product-tile-name">{BRAND[p.key]}</bdi>
          <span className="product-tile-desc"><Label id={p.audience} claim="P2C01" /></span>
        </span>
        <ArrowRight className="product-tile-arrow" aria-hidden="true" strokeWidth={1.8} />
      </a>
    </li>)}</ul>
  </nav>;
}

export function HomeHero() {
  const { t } = useMarketing();
  return <section className="hero signal home-hero" aria-labelledby="hero-title">
    <div className="wrap hero-grid">
      <div className="hero-copy">
        <span className="eyebrow hero-eyebrow"><bdi dir="ltr">SHOPYXI</bdi><span className="hero-eyebrow-rule" aria-hidden="true" />{t('four')}</span>
        <h1 id="hero-title" data-claim-fragment="C01">
          <span className="hero-line">{t('heroline1')}</span>{' '}
          <span className="hero-line hero-line-accent"><Mixed text={t('heroline2')} /></span>
        </h1>
        <p className="hero-lede" data-claim-fragment="C01"><Mixed text={t('herolede')} /></p>
        <HeroActions />
        <HeroValuePoints />
      </div>
      <HeroWindows />
    </div>
    <div className="wrap">
      <HeroProductNav />
    </div>
  </section>;
}
