import type { LandingTemplateProps } from './TemplateRegistry';
import { LandingFrame, ProductGallery, ProductTitle, ProductSpecifications, PurchasePanel } from './ProductParts';

export function MinimalFastCheckoutTemplate(props: LandingTemplateProps) {
    return <LandingFrame props={props} template="minimal"><main className="lp-product-minimal"><ProductGallery props={props} compact /><div className="lp-product-details"><ProductTitle props={props} /><ProductSpecifications props={props} minimal /><PurchasePanel props={props} /></div></main></LandingFrame>;
}
