"""Offline rendering only. Inputs are reference names/paths, NEVER Secret values.
Usage: python render.py approved-references.json output-directory
This is NOT kubectl, does not connect to a cluster and cannot apply resources.
"""
import json,pathlib,re,sys,copy
base=pathlib.Path(__file__).parent
def render(inputs):
 document=json.loads((base/'application.template.json').read_text())
 required=json.loads((base/'required-input-contract.json').read_text())
 if inputs.get('replicas',1)!=1:raise ValueError('Exactly one API replica required')
 refs=inputs['references']
 def replace(value):
  if isinstance(value,dict):return {k:replace(v) for k,v in value.items()}
  if isinstance(value,list):return [replace(v) for v in value]
  if isinstance(value,str) and value.startswith(('REQUIRED_','REQUIRES_NEW_')):
   if value not in refs:raise ValueError('Missing approved reference '+value)
   replacement=refs[value]
   if not isinstance(replacement,str) or not replacement:raise ValueError('Empty reference '+value)
   if value.startswith('REQUIRES_NEW_') and not re.fullmatch(r'[a-zA-Z0-9./:_-]+@sha256:[a-f0-9]{64}',replacement):raise ValueError('Immutable rebuilt image digest required')
   return replacement
  return value
 document=replace(document)
 api=next(r for r in document['items'] if r['kind']=='Deployment' and r['metadata']['name']=='shopixy-api')
 pod=api['spec']['template']['spec'];container=pod['containers'][0]
 # Required retained decryptors are explicit, numbered, read-only and independent
 # from the active mount. Empty list needs an explicit non-secret custodian attestation.
 if not inputs.get('legacy_material_attestation'):raise ValueError('Legacy material custody attestation required')
 for index,retired in enumerate(inputs['retained_decryptors']):
  name='dp-retired-'+str(index);mount='/var/run/shopixy/'+name
  pod['volumes'].append({'name':name,'secret':{'secretName':retired['secret_name'],'defaultMode':288,'items':[{'key':retired['certificate_key'],'path':'certificate.pem'},{'key':retired['private_key_key'],'path':'private-key.pem'}]}})
  container['volumeMounts'].append({'name':name,'mountPath':mount,'readOnly':True})
  container['env'] += [{'name':f'DataProtection__DecryptionCertificates__{index}__CertificatePath','value':mount+'/certificate.pem'},{'name':f'DataProtection__DecryptionCertificates__{index}__PrivateKeyPath','value':mount+'/private-key.pem'}]
 return document
if __name__=='__main__':
 inputs=json.loads(pathlib.Path(sys.argv[1]).read_text());out=pathlib.Path(sys.argv[2]);out.mkdir(parents=True,exist_ok=True)
 document=render(inputs);(out/'application.json').write_text(json.dumps(document,indent=2)+'\n')
 print('Offline manifest rendered. Separate approval, schema, prerequisite and integration validation remain mandatory.')
