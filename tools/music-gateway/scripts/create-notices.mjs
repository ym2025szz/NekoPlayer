import { readFile, readdir, mkdir, writeFile, copyFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const lock = JSON.parse(await readFile(path.join(root, 'package-lock.json'), 'utf8'));
const out = path.join(root, 'LICENSES'); await mkdir(out, { recursive: true });
const packages = [];
for (const [location, locked] of Object.entries(lock.packages)) {
  if (!location.startsWith('node_modules/')) continue;
  const folder = path.join(root, location);
  const packageBytes = await readFile(path.join(folder, 'package.json'));
  const metadata = JSON.parse(packageBytes);
  const name = metadata.name || location.split('node_modules/').at(-1);
  const target = location.replaceAll('/', '__').replaceAll('@', '').replaceAll(':', '_');
  const licenseFiles = (await readdir(folder, { withFileTypes: true })).filter(f => f.isFile() && /^(licen[cs]e|copying|copyright|notice)([.\s_-].*)?$/i.test(f.name)).map(f => f.name);
  const copied = [];
  if (licenseFiles.length) {
    await mkdir(path.join(out, target), { recursive: true });
    for (const file of licenseFiles) {
      await copyFile(path.join(folder,file), path.join(out,target,file));
      const bytes = await readFile(path.join(folder,file));
      copied.push({ file: `${target}/${file}`, sha256: createHash('sha256').update(bytes).digest('hex') });
    }
  }
  packages.push({ name, version: metadata.version, installedPath: location, license: metadata.license || locked.license || 'unspecified',
    repository: typeof metadata.repository === 'string' ? metadata.repository : metadata.repository?.url,
    resolved: locked.resolved, integrity: locked.integrity,
    packageJsonSha256: createHash('sha256').update(packageBytes).digest('hex'), licenseFiles: copied });
}
packages.sort((a,b) => a.name.localeCompare(b.name) || a.installedPath.localeCompare(b.installedPath));
await writeFile(path.join(out,'THIRD-PARTY-NOTICES.json'), JSON.stringify({ generatedAt: new Date().toISOString(), sdkPins: {
  netease: '@neteasecloudmusicapienhanced/api@4.41.1', qq: '@sansenjian/qq-music-api@2.6.0',
  kugou: 'MakcRe/KuGouMusicApi@da5ccfd9304c043085a2fd18e94ebc5c315044ab',
  qishui: 'guowenye/qishui-api@e409c10fe7441a10d370da7da61d62bbc1e5126c',
}, packages }, null, 2) + '\n');
await writeFile(path.join(out,'THIRD-PARTY-NOTICES.md'), '# Music gateway dependency notices\n\nFour platform SDK sources and their runtime dependencies are recorded here. Original license and notice files are copied unchanged into package-specific folders. Exact tarball integrity values and SHA-256 digests are in THIRD-PARTY-NOTICES.json. SDK installation scripts are disabled.\n\n' + packages.map(p => `- ${p.name} ${p.version} — ${typeof p.license === 'string' ? p.license : JSON.stringify(p.license)}${p.licenseFiles.length ? `; ${p.licenseFiles.map(f => `[${path.basename(f.file)}](${f.file})`).join(', ')}` : '; license declaration in package.json (no separate license file shipped)'}`).join('\n') + '\n');
process.stdout.write(JSON.stringify({ packages: packages.length, copiedLicenseFiles: packages.reduce((n,p) => n + p.licenseFiles.length,0), missingSeparateLicenseFiles: packages.filter(p => !p.licenseFiles.length).map(p => p.name) }) + '\n');
