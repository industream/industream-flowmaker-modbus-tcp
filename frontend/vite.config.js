import { svelte } from '@sveltejs/vite-plugin-svelte';
import { resolve } from 'path';

const name = process.env.COMPONENT;
if (!name) throw new Error('COMPONENT env var required');

export default {
    plugins: [
        svelte({
            compilerOptions: {
                css: 'injected'
            }
        })
    ],
    build: {
        outDir: resolve(process.cwd(), './dist'),
        emptyOutDir: false,
        cssCodeSplit: false,
        lib: {
            entry: resolve(process.cwd(), 'src', name, 'main.js'),
            formats: ['iife'],
            name: `FlowBoxUIConfig`,
            fileName: () => `config.${name}.jsbundle.js`
        },
        rollupOptions: {
            output: {
                inlineDynamicImports: true
            }
        },
        minify: true
    }
};
