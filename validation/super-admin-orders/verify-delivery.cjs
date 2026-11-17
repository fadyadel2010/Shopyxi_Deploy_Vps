// Read-only handoff verification. Prints the final commit without self-referential files.
const fs=require('node:fs')
const {execFileSync}=require('node:child_process')
const {createHash}=require('node:crypto')
const git=(...args)=>execFileSync('git',args,{encoding:'utf8'}).trim()
const read=name=>JSON.parse(fs.readFileSync(`${__dirname}/${name}`,'utf8'))
const release=read('release-inputs.json'),acceptance=read('production-acceptance.json'),build=read('build-gates.json')
const localHead=git('rev-parse','HEAD')
const branch='super-admin/orders-forensic-audit-20261117'
const remoteTip=git('ls-remote','origin',`refs/heads/${branch}`).split(/\s+/)[0]
const evidenceCommit=git('log','-1','--format=%H','--','validation/super-admin-orders/production-acceptance.json')
const sourceRoots=['PointOfSale.API','PointOfSale.Core','PointOfSale.Infrastructure','PointOfSale.Web',
    'PointOfSale.Storefront','PointOfSale.Tests','theme-authority']
const sourceUnchangedFromDeployedCommit=!git('diff','--name-only',release.sourceCommit,'HEAD','--',...sourceRoots)
const sealedSourceFilesMatched=Object.entries(build.sourceFilesSha256).every(([p,h])=>
    createHash('sha256').update(fs.readFileSync(p)).digest('hex')===h)
const rows=execFileSync('ssh',['-o','BatchMode=yes','-o','ConnectTimeout=15','shopixy-vps',
    'kubectl -n shopixy-app get deployments shopixy-api shopixy-web -o custom-columns=NAME:.metadata.name,IMAGE:.spec.template.spec.containers[0].image --no-headers'],
    {encoding:'utf8',timeout:120000}).trim().split(/\r?\n/).map(l=>l.trim().split(/\s+/))
const liveImages=Object.fromEntries(rows)
const runtimeImagesMatched=Object.entries(release.components).every(([name,c])=>
    liveImages[`shopixy-${name}`]===`localhost/shopixy/${name}@${c.manifestDigest}`)
git('merge-base','--is-ancestor',evidenceCommit,localHead)
git('merge-base','--is-ancestor','181b8a0f69ff36545acd09618b41b0dd0006900d',localHead)
const result={checkedAtUtc:new Date().toISOString(),branch,localHead,remoteTip,
    checkoutClean:!git('status','--porcelain','--untracked-files=all'),
    deployedApplicationCommit:release.sourceCommit,acceptanceEvidenceCommit:evidenceCommit,
    acceptancePassed:acceptance.pass===true,sourceUnchangedFromDeployedCommit,
    sealedSourceFilesMatched,runtimeImagesMatched,liveImages,
    prCreationUrl:`https://github.com/fadyadel2010/Shopyxi_Deploy_Vps/pull/new/${branch}`}
result.pass=result.localHead===result.remoteTip&&result.checkoutClean&&result.acceptancePassed&&
    result.deployedApplicationCommit===acceptance.applicationCommit&&sourceUnchangedFromDeployedCommit&&
    sealedSourceFilesMatched&&runtimeImagesMatched
console.log(JSON.stringify(result,null,2))
if(!result.pass)process.exitCode=1
