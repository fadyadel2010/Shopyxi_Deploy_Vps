const d = db.getSiblingDB(__DATABASE_NAME__);
const c = d.getCollection('affiliate_order_conversions');
if (c.countDocuments({}) !== 0) throw new Error('Review existing conversion rows before schema provisioning');
c.createIndex({affiliateAccountId:1,convertedAtUtc:1,tenantId:1,productId:1},{name:'idx_affiliate_conversion_timeline'});
c.createIndex({clickId:1},{name:'idx_affiliate_conversion_click_unique',unique:true,partialFilterExpression:{clickId:{$type:'string'}}});
const indexes = c.getIndexes();
const timeline = indexes.find(x=>x.name==='idx_affiliate_conversion_timeline');
const click = indexes.find(x=>x.name==='idx_affiliate_conversion_click_unique');
if (!timeline || JSON.stringify(timeline.key)!==JSON.stringify({affiliateAccountId:1,convertedAtUtc:1,tenantId:1,productId:1}) ||
    !click || !click.unique || JSON.stringify(click.key)!==JSON.stringify({clickId:1}) ||
    JSON.stringify(click.partialFilterExpression)!==JSON.stringify({clickId:{$type:'string'}}))
  throw new Error('Analytics index verification failed');
print(JSON.stringify({indexesVerified:2,conversionDocuments:c.countDocuments({}),businessRowsChanged:false}));
