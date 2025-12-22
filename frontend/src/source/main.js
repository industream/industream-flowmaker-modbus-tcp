import ConfigForm from './ConfigForm.svelte';
import { FlowBoxSvelteUI } from '@industream/flowmaker-flowbox-ui';

register('box-config-frontend', new FlowBoxSvelteUI(ConfigForm));
