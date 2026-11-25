import React from 'react';
import { ChevronDown, Globe, Menu, UserRound } from 'lucide-react';
import { Claim, Label, Mixed, PageLink, Logo, products, useMarketing } from './core.jsx';
import { ProductGlyph, productHref } from './homepage.jsx';
import { accessDestinations, pagePath } from '../routes.mjs';

/** Products mega menu. Native <details>: keyboard operable, no focus trap. */
function ProductMenu() {
  const { t, locale } = useMarketing();
  return <details className="nav-menu" data-menu="">
    <summary className="nav-menu-summary"><span>{t('products')}</span>
      <ChevronDown className="nav-chevron" aria-hidden="true" size={15} strokeWidth={2.2} />
    </summary>
    <div className="nav-panel">
      <ul className="nav-panel-list">{products.map(p => <li key={p.key}>
        <a className="nav-panel-link" href={productHref(locale, p.key)}>
          <span className={`nav-panel-icon tile-icon-${p.key}`} aria-hidden="true"><ProductGlyph product={p.key} size={17} /></span>
          <span className="nav-panel-body">
            <bdi className="nav-panel-index" dir="ltr">{p.n}</bdi>
            <span className="nav-panel-name"><Mixed text={t(p.key)} /></span>
            <span className="nav-panel-desc"><Label id={p.audience} claim="P2C01" /></span>
          </span>
        </a>
      </li>)}</ul>
      <a className="nav-panel-all" href="#products">{t('allproducts')}</a>
    </div>
  </details>;
}

/** Mobile navigation panel: the same verified destinations, no duplicated login. */
function MobilePanel() {
  const { t, locale } = useMarketing();
  return <nav className="access-panel mobile-panel" aria-label={t('menu')}>
    <p className="mobile-panel-title">{t('products')}</p>
    <ul className="mobile-panel-products">{products.map(p => <li key={p.key}>
      <a href={productHref(locale, p.key)}>
        <span className={`mobile-panel-icon tile-icon-${p.key}`} aria-hidden="true"><ProductGlyph product={p.key} size={16} /></span>
        <span className="mobile-panel-body"><bdi dir="ltr">{p.n}</bdi><span><Mixed text={t(p.key)} /></span>
          <span className="mobile-panel-desc"><Label id={p.audience} claim="P2C01" /></span></span>
      </a>
    </li>)}</ul>
    <ul className="mobile-panel-links">
      <li><PageLink page="platform" className="" >{t('platform')}</PageLink></li>
      <li><a href="#products">{t('allproducts')}</a></li>
    </ul>
    <a className="button mobile-panel-cta" href={`${pagePath(locale, 'home')}#products`}>{t('exploreproducts')}</a>
  </nav>;
}

export function Header() {
  const { t, locale, equivalent, page, publicRelease } = useMarketing();
  const productsHref = page === 'home' ? '#products' : `${pagePath(locale, 'home')}#products`;
  return <>
    <a className="skip" href="#main">{t('skip')}</a>
    {!publicRelease && <aside className="review" aria-label={locale === 'ar' ? 'حالة المراجعة الداخلية' : 'Internal review status'}>
      <div className="wrap review-inner">
        <div className="review-label"><Label id="concept" /><small>{t('draft')}</small></div>
        <bdi className="variant-label" dir="ltr">A+01 / Signal Stage</bdi>
      </div>
    </aside>}
    <header className="header"><div className="wrap header-inner">
      <Logo />
      <nav className="nav" aria-label={t('menu')}>
        <ProductMenu />
        <PageLink page="platform" className="nav-link">{t('platform')}</PageLink>
      </nav>
      <div className="utilities">
        <details className="access" id="customer-access" data-menu="">
          <summary className="access-summary">
            <UserRound className="access-icon" aria-hidden="true" size={17} strokeWidth={1.8} />
            <span className="access-label">{t('access')}</span>
          </summary>
          <div className="access-panel">
            <Claim id="C48" />
            {accessDestinations.map((url, i) => <a key={url} href={url}>
              <Mixed text={t('accesslabels')[i]} />
              <span data-claim-fragment="P2C02"><Mixed text={t('accessdesc')[i]} /></span>
            </a>)}
            <p className="store-account"><strong>{t('accesslabels')[3]}</strong><br />
              <span data-claim-fragment="P2C02"><Mixed text={t('accessdesc')[3]} /></span>
            </p>
          </div>
        </details>
        <a className="plain locale" id="locale" href={`${equivalent}#locale`} hrefLang={locale === 'en' ? 'ar' : 'en'}
          lang={locale === 'en' ? 'ar' : 'en'} dir={locale === 'en' ? 'rtl' : 'ltr'}>
          <Globe className="locale-icon" aria-hidden="true" size={17} strokeWidth={1.8} />
          <span>{t('language')}</span>
        </a>
        <a className="button header-demo" href={productsHref} data-claim-fragment="C40">
          <span>{t('exploreproducts')}</span>
        </a>
      </div>
      <details className="mobile-nav" data-menu="">
        <summary><Menu className="mobile-nav-icon" aria-hidden="true" size={18} strokeWidth={1.9} /><span>{t('menu')}</span></summary>
        <MobilePanel />
      </details>
    </div></header>
  </>;
}
export function Demo() {
  const { t, publicRelease } = useMarketing();
  if (publicRelease) return <section className="section soft" id="explore" tabIndex="-1">
    <div className="wrap conversion"><div><span className="eyebrow">{t('four')}</span><h2>{t('choose')}</h2></div>
      <div className="related-list">{products.map(p => <PageLink key={p.key} page={p.key}><Mixed text={t(p.key)} /></PageLink>)}</div>
    </div>
  </section>;
  return <section className="section soft" id="demo" tabIndex="-1" aria-labelledby="demo-title">
    <div className="wrap conversion">
      <div><Claim id="C39" as="span" className="eyebrow" /><h2 id="demo-title">{t('demo')}</h2>
        <Claim id="C41" />
        <p className="small muted" id="demo-pending" data-claim-fragment="P2C04">{t('pending')}</p>
      </div>
      <div className="reserved"><span className="pending-tag">{t('gate')}</span><h3 className="reserved-title">{t('reserve')}</h3>
        <Label id="reserved" as="p" claim="P2C04" />
        <button className="button" type="button" disabled><Claim id="C39" as="span" /></button>
        <p className="small">{t('legal')}</p>
      </div>
    </div>
  </section>;
}
export function Footer() {
  const { t, publicRelease, page } = useMarketing();
  // Phase 1G premium footer. Two named navigation groups, a brand signature and
  // no legal/contact content. The review-only Demo link stays on non-Homepage
  // routes; the Homepage keeps its single Free Experience CTA above this footer.
  return <footer className="sf">
    <div className="sf-ambient" aria-hidden="true"><span className="sf-orb sf-orb-a" aria-hidden="true" /><span className="sf-orb sf-orb-b" aria-hidden="true" /></div>
    <div className="wrap sf-wrap">
      <div className="sf-grid">
        <div className="sf-brand"><Logo /><Claim id="C01" as="p" className="sf-desc" /></div>
        <div className="sf-group sf-explore">
          <nav aria-labelledby="sf-explore-title">
            <h2 className="sf-heading" id="sf-explore-title">{t('footerExplore')}</h2>
            <ul className="sf-list">
              <li><PageLink page="home" className="sf-link">{t('home')}</PageLink></li>
              <li><PageLink page="platform" className="sf-link">{t('platform')}</PageLink></li>
            </ul>
          </nav>
          {!publicRelease && page !== 'home' && <a className="sf-review-link" href="#demo"><Claim id="C39" as="span" /></a>}
        </div>
        <nav className="sf-group sf-products" aria-labelledby="sf-products-title">
          <h2 className="sf-heading" id="sf-products-title">{t('products')}</h2>
          <ul className="sf-list sf-product-list">
            {products.map(p => <li key={p.key} className="sf-product">
              <span className={`sf-dot sf-dot-${p.key}`} aria-hidden="true" />
              <PageLink page={p.key} className="sf-link"><Mixed text={t(p.key)} /></PageLink>
            </li>)}
          </ul>
        </nav>
      </div>
      <div className="sf-signature"><img src="/assets/shopyxi-logo.png" alt="" width="2400" height="747" decoding="async" /></div>
    </div>
  </footer>;
}