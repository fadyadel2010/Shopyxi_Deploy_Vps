import React from 'react';
import { Layers, Monitor, Rocket, UserRoundPlus, Zap } from 'lucide-react';
import { Claim, Label, Mixed, products, useMarketing } from './core.jsx';
import { CaptureImage } from './product-screen.jsx';
import { ProductGlyph } from './homepage.jsx';
import screenshots from '../content/product-screenshots.json';
import copy from '../content/composition.json';
import { registrationDestination } from '../routes.mjs';

/**
 * Phase 1F-D — Free Experience CTA.
 *
 * Replaces the review-only homepage Demo placeholder with a permanent,
 * self-registration invitation. The layered windows below are the audited,
 * unmodified captures from product-screenshots.json; the four floating tiles
 * are decorative product glyphs, never invented screenshots.
 */

const BENEFIT_ICON = [Zap, Layers, Monitor];
const BENEFIT_LABEL = ['fxBenefitAccount', 'fxBenefitProducts', 'fxBenefitWorkflows'];

// Back-to-front order. Each entry names an existing manifest variant; a missing
// variant simply omits that window rather than fabricating one.
const WINDOWS = [
  { product: 'commercehub', variant: 'folio' },
  { product: 'storefront', variant: 'folio' },
  { product: 'affiliate', variant: 'desktop' },
  { product: 'pos', variant: 'desktop' }
];

function CompositionWindow({ product, variant }) {
  const { locale } = useMarketing();
  const screen = screenshots.products[product][locale];
  const image = screen[variant];
  if (!image) return null;
  return <figure className={`fx-window fx-window-${product}`} data-fx-window={product}>
    <span className="fx-window-bar" aria-hidden="true">
      <span className="fx-window-lights"><i /><i /><i /></span>
      <span className="fx-window-pill" />
    </span>
    <span className="fx-window-shot">
      <CaptureImage image={image} alt={variant === 'folio' ? screen.detailAlt : screen.alt} />
    </span>
  </figure>;
}

function ProductTiles() {
  return <ul className="fx-tiles" aria-hidden="true">{products.map(product => <li key={product.key}>
    <span className={`fx-tile fx-tile-${product.key}`} data-fx-tile={product.key}>
      <ProductGlyph product={product.key} size={20} />
    </span>
  </li>)}</ul>;
}

export function FreeExperienceCTA() {
  const { locale, t } = useMarketing();
  return <section className="fx-section" id="free-experience" aria-labelledby="fx-title">
    <div className="fx-shell">
      <div className="fx-panel">
        <span className="fx-panel-glow" aria-hidden="true" />
        <div className="fx-copy">
          <span className="fx-badge"><Rocket className="fx-badge-icon" size={15} strokeWidth={2.1} aria-hidden="true" />
            <Label id="fxBadge" className="fx-badge-text" /></span>
          <h2 className="fx-title" id="fx-title">
            <Claim id="C52" as="span" className="fx-title-line" />
            <Claim id="C53" as="span" className="fx-title-accent" />
          </h2>
          <Claim id="C54" as="p" className="fx-lede" />
          <a className="fx-cta" href={registrationDestination} data-register-destination="">
            <UserRoundPlus className="fx-cta-icon" size={21} strokeWidth={2} aria-hidden="true" />
            <Claim id="C55" as="span" />
          </a>
          <ul className="fx-benefits" aria-label={t('fxBenefitsLabel')}>
            {BENEFIT_LABEL.map((id, i) => {
              const Icon = BENEFIT_ICON[i];
              return <li className="fx-benefit" key={id} data-benefit={id}>
                <Icon className="fx-benefit-icon" size={17} strokeWidth={1.9} aria-hidden="true" />
                <span><Mixed text={t(id)} /></span>
              </li>;
            })}
          </ul>
        </div>
        <div className="fx-art" role="group" aria-label={copy[locale].compositionLabel}>
          <div className="fx-stage">
            <span className="fx-glow" aria-hidden="true" />
            {WINDOWS.map(window => <CompositionWindow key={window.product} {...window} />)}
            <ProductTiles />
          </div>
        </div>
      </div>
    </div>
  </section>;
}