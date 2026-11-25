import React from 'react';
import { ArrowRight, Layers, Megaphone, Monitor, ShoppingBag, ShoppingCart, Store, Users } from 'lucide-react';
import { labels, Mixed, useMarketing } from './core.jsx';
import { CaptureImage, ScreenshotDisclosure } from './product-screen.jsx';
import { pagePath } from '../routes.mjs';
import screenshots from '../content/product-screenshots.json';

/**
 * Phase 1D — Business Capabilities.
 *
 * Three wide editorial bands replace the former homepage Capabilities block.
 * Every application view is an independent, unmodified manifest capture held in
 * CSS-only browser chrome. Nothing is painted over screenshot pixels.
 */
const PANELS = [
  {
    key: 'pos',
    n: '01',
    accent: 'blue',
    icon: ShoppingCart,
    edgeIcon: Store,
    title: 'bcPosTitle',
    desc: 'bcPosDesc',
    claim: 'C07',
    screens: [{ product: 'pos', cta: 'bcPosCta', href: 'pos', icon: Monitor, claim: 'C07' }]
  },
  {
    key: 'storefront',
    n: '02',
    accent: 'violet',
    icon: ShoppingBag,
    edgeIcon: null,
    title: 'bcStoreTitle',
    desc: 'bcStoreDesc',
    claim: 'C17',
    screens: [{ product: 'storefront', cta: 'bcStoreCta', href: 'storefront', icon: Store, claim: 'C17' }]
  },
  {
    key: 'channels',
    n: '03',
    accent: 'green',
    icon: Layers,
    edgeIcon: Megaphone,
    title: 'bcChannelsTitle',
    desc: 'bcChannelsDesc',
    claim: 'C04',
    screens: [
      { product: 'affiliate', cta: 'bcAffiliateCta', href: 'affiliate', icon: Users, claim: 'C04' },
      { product: 'commercehub', cta: 'bcCommercehubCta', href: 'commercehub', icon: Store, claim: 'C05' }
    ]
  }
];

function CaptureWindow({ screen, locale, priority }) {
  const image = screenshots.products[screen.product][locale].desktop;
  if (!image) return null;
  const product = screenshots.products[screen.product][locale];
  return <figure className={`bc-shot bc-shot-${screen.product}`}
    data-screen-product={screen.product} data-screen-candidate={image.candidate} data-screen-variant="desktop">
    <div className="bc-window-bar" aria-hidden="true">
      <span className="bc-lights"><i /><i /><i /></span>
      <span className="bc-window-label"><bdi dir="ltr">{labels.en[screen.product]}</bdi></span>
    </div>
    <CaptureImage image={image} alt={product.alt} priority={priority} />
  </figure>;
}

function Panel({ panel }) {
  const { t, locale } = useMarketing();
  const PanelIcon = panel.icon;
  const EdgeIcon = panel.edgeIcon;
  const titleId = `bc-title-${panel.key}`;
  return <article className={`bc-panel bc-panel-${panel.key} bc-accent-${panel.accent}`}
    data-bc-panel={panel.key} aria-labelledby={titleId}>
    {EdgeIcon && <span className="bc-edge" aria-hidden="true"><EdgeIcon size={32} strokeWidth={1.7} /></span>}
    <span className="bc-wash" aria-hidden="true" />
    <span className="bc-wash-tail" aria-hidden="true" />
    <div className="bc-visual">
      <span className="bc-glow bc-glow-a" aria-hidden="true" />
      <span className="bc-glow bc-glow-b" aria-hidden="true" />
      <span className="bc-haze" aria-hidden="true" />
      <div className="bc-stage">
        {panel.screens.map((screen, index) => <CaptureWindow key={screen.product} screen={screen}
          locale={locale} priority={index === 0 && panel.key === 'pos'} />)}
        <span className="bc-float" aria-hidden="true"><PanelIcon size={34} strokeWidth={1.8} /></span>
      </div>
    </div>
    <div className="bc-copy">
      <span className="bc-number" aria-hidden="true">{panel.n}</span>
      <h3 id={titleId}>{t(panel.title)}</h3>
      <p className="bc-desc" data-claim-fragment={panel.claim}>{t(panel.desc)}</p>
      <div className={`bc-actions${panel.screens.length > 1 ? ' bc-actions-channels' : ''}`}>
        {panel.screens.map(screen => {
          const CtaIcon = screen.icon;
          return <a key={screen.product} className={`bc-cta bc-cta-${screen.product}`}
            href={`${pagePath(locale, screen.href)}#main`} data-claim-fragment={screen.claim}>
            <CtaIcon className="bc-cta-icon" size={18} strokeWidth={1.9} aria-hidden="true" />
            <span><Mixed text={t(screen.cta)} /></span>
            <ArrowRight className="bc-cta-arrow" size={18} strokeWidth={2.2} aria-hidden="true" />
          </a>;
        })}
      </div>
    </div>
  </article>;
}

export function BusinessCapabilitiesShowcase() {
  const { t } = useMarketing();
  return <section className="bc-showcase" id="capabilities" aria-labelledby="bc-title">
    <span className="bc-orb bc-orb-a" aria-hidden="true" />
    <span className="bc-orb bc-orb-b" aria-hidden="true" />
    <div className="bc-shell">
      <header className="bc-intro">
        <span className="bc-eyebrow"><Layers size={15} strokeWidth={2} aria-hidden="true" /><Mixed text={t('bcEyebrow')} /></span>
        <h2 id="bc-title">{t('bcHeading')}</h2>
        <p className="bc-intro-text">{t('bcIntro')}</p>
      </header>
      <div className="bc-panels">
        {PANELS.map(panel => <Panel key={panel.key} panel={panel} />)}
      </div>
      <ScreenshotDisclosure />
    </div>
  </section>;
}
