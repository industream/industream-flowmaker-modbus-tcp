<script>
    import { flowMakerBridge, getValue } from '@industream/flowmaker-flowbox-ui';
    import { DCSourceConnection } from '@industream/flowmaker-flowbox-ui-components';
    import RegisterWizard from './RegisterWizard.svelte';

    const props = $props();
    const { context, values, validity, emit, jsonEnv } = flowMakerBridge(props);

    // Get DataCatalog config from environment variables
    const dcConfig = jsonEnv('datacatalog');

    let lastSourceConnectionId = null;
    let lastRegisters = null;
    let registersError = $state('');
    let showWizard = $state(true);

    /**
     * Validate Registers (one per line)
     * Valid format: name:type:address[:datatype[:quantity]]
     */
    function validateRegisters(registersText) {
        if (!registersText || registersText.trim().length === 0) {
            return { valid: false, error: 'At least one register is required' };
        }

        const lines = registersText.split('\n').map(l => l.trim()).filter(l => l.length > 0 && !l.startsWith('#'));

        if (lines.length === 0) {
            return { valid: false, error: 'At least one register is required' };
        }

        // Basic validation: format is name:type:address[:datatype[:quantity]]
        const registerPattern = /^[a-zA-Z_][a-zA-Z0-9_]*:(coil|discrete|holding|input):\d+(:[\w]+)?(:[\d]+)?$/i;
        for (let i = 0; i < lines.length; i++) {
            if (!registerPattern.test(lines[i])) {
                return { valid: false, error: `Line ${i + 1}: Invalid format. Use name:type:address[:datatype[:quantity]]` };
            }
        }

        return { valid: true, error: '' };
    }

    const onSourceSelect = (conn) => {
        $values.sourceConnectionId = conn?.id ?? '';
        updateValidity();
    };

    const updateValidity = () => {
        const sourceInvalid = !$values?.sourceConnectionId;
        const registersValidation = validateRegisters($values?.registers);

        registersError = registersValidation.error;

        $validity.sourceConnectionId = sourceInvalid;
        $validity.registers = !registersValidation.valid;
        emit('validity', $validity);
    };

    // Check validity when sourceConnectionId or registers change
    $effect(() => {
        const currentId = $values?.sourceConnectionId;
        const currentRegisters = $values?.registers;

        if (currentId !== lastSourceConnectionId || currentRegisters !== lastRegisters) {
            lastSourceConnectionId = currentId;
            lastRegisters = currentRegisters;
            updateValidity();
        }
    });

    const onRegistersChange = (e) => {
        $values.registers = getValue(e);
        updateValidity();
    };

    const onAddRegister = (registerDef) => {
        const currentRegisters = $values?.registers ?? '';
        const separator = currentRegisters.trim() ? '\n' : '';
        $values.registers = currentRegisters + separator + registerDef;
        updateValidity();
    };

    const toggleWizard = () => {
        showWizard = !showWizard;
    };

</script>

<div class="config-form">
    <div class="section">
        <h4>Connection</h4>
        <p class="section-description">Select a Modbus TCP server from DataCatalog. Connection parameters (host, port, slave ID) are stored in the source connection.</p>
        <div class="form-row">
            <DCSourceConnection
                dcapiurl={$dcConfig?.url}
                sourcetypefilter="Modbus-TCP"
                initialselection={$values?.sourceConnectionId ?? ''}
                onsourceselect={onSourceSelect}
            />
        </div>
    </div>

    <div class="section">
        <div class="section-header">
            <h4>Registers</h4>
            <button class="toggle-wizard-btn" onclick={toggleWizard}>
                {showWizard ? 'Manual' : 'Wizard'}
            </button>
        </div>

        {#if showWizard}
            <RegisterWizard {onAddRegister} />
        {/if}

        <div class="form-row">
            <label for="registers" class="cds--label">
                Modbus registers to read (one per line)
                {#if showWizard}
                    <span class="label-hint">— registers added by wizard appear here</span>
                {/if}
            </label>
            <cds-textarea
                id="registers"
                value={$values?.registers ?? ''}
                rows="8"
                placeholder="# Format: name:type:address[:datatype[:quantity]]&#10;temperature:holding:0:float32&#10;pressure:holding:2:float32&#10;status:coil:100:bool&#10;counters:holding:10:uint32:5"
                invalid={!!registersError}
                invalid-text={registersError}
                oninput={onRegistersChange}>
            </cds-textarea>
            <small class="helper-text">
                <strong>Types:</strong> coil, discrete, holding, input<br/>
                <strong>DataTypes:</strong> uint16, int16, uint32, int32, float32, uint64, int64, float64, bool, string<br/>
                <strong>Quantity:</strong> Number of consecutive values to read (optional, default: 1)
            </small>
        </div>
    </div>

</div>

<style>
    .config-form {
        display: flex;
        flex-direction: column;
        gap: 1.5rem;
        padding: 1rem;
    }

    .section {
        display: flex;
        flex-direction: column;
        gap: 0.75rem;
    }

    .section h4 {
        margin: 0;
    }

    .section-header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        padding-bottom: 0.5rem;
        border-bottom: 1px solid var(--cds-border-subtle-01, #e0e0e0);
    }

    .toggle-wizard-btn {
        border: none;
        padding: 0.375rem 0.75rem;
        border-radius: 4px;
        font-size: 0.75rem;
        cursor: pointer;
    }

    .section-description {
        margin: 0;
        font-size: 0.875rem;
        color: var(--cds-text-secondary, #525252);
    }

    .form-row {
        display: flex;
        flex-direction: column;
        gap: 0.5rem;
    }

    .helper-text {
        color: var(--cds-text-secondary, #525252);
        font-size: 0.75rem;
        margin-top: 0.25rem;
    }

    .label-hint {
        font-weight: normal;
        color: var(--cds-text-helper, #6f6f6f);
        font-style: italic;
    }
</style>
