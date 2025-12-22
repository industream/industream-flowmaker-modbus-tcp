<script>
    const props = $props();
    const { onAddRegister } = props;

    // Function codes mapping
    const functionCodes = [
        { code: '01', label: 'FC01 - Coils (Read/Write)', type: 'coil', defaultDataType: 'bool' },
        { code: '02', label: 'FC02 - Discrete Inputs (Read)', type: 'discrete', defaultDataType: 'bool' },
        { code: '03', label: 'FC03 - Holding Registers (Read/Write)', type: 'holding', defaultDataType: 'uint16' },
        { code: '04', label: 'FC04 - Input Registers (Read)', type: 'input', defaultDataType: 'uint16' }
    ];

    const dataTypes = [
        { value: 'bool', label: 'Boolean', registers: 1 },
        { value: 'uint16', label: 'UInt16 (16-bit unsigned)', registers: 1 },
        { value: 'int16', label: 'Int16 (16-bit signed)', registers: 1 },
        { value: 'uint32', label: 'UInt32 (32-bit unsigned)', registers: 2 },
        { value: 'int32', label: 'Int32 (32-bit signed)', registers: 2 },
        { value: 'float32', label: 'Float32 (32-bit float)', registers: 2 },
        { value: 'uint64', label: 'UInt64 (64-bit unsigned)', registers: 4 },
        { value: 'int64', label: 'Int64 (64-bit signed)', registers: 4 },
        { value: 'float64', label: 'Float64 (64-bit float)', registers: 4 },
        { value: 'string', label: 'String', registers: 1 }
    ];

    // Form state
    let selectedFc = $state('04');
    let registerName = $state('');
    let startAddress = $state(0);
    let selectedDataType = $state('uint16');
    let quantity = $state(1);

    // Derived values
    let selectedFcInfo = $derived(functionCodes.find(fc => fc.code === selectedFc));
    let selectedDataTypeInfo = $derived(dataTypes.find(dt => dt.value === selectedDataType));
    let totalRegisters = $derived((selectedDataTypeInfo?.registers || 1) * quantity);

    // Generated register definition
    let generatedDefinition = $derived(() => {
        if (!registerName.trim()) return '';
        const fc = selectedFcInfo;
        let def = `${registerName}:${fc?.type}:${startAddress}:${selectedDataType}`;
        if (quantity > 1) {
            def += `:${quantity}`;
        }
        return def;
    });

    // Preview of all addresses that will be read
    let addressPreview = $derived(() => {
        if (quantity <= 1) return `Address ${startAddress}`;
        const regsPerValue = selectedDataTypeInfo?.registers || 1;
        const addresses = [];
        for (let i = 0; i < quantity; i++) {
            const addr = startAddress + (i * regsPerValue);
            addresses.push(addr);
        }
        if (addresses.length <= 8) {
            return `Addresses: ${addresses.join(', ')}`;
        }
        return `Addresses: ${addresses.slice(0, 4).join(', ')} ... ${addresses.slice(-2).join(', ')} (${addresses.length} values)`;
    });

    // Update default data type when function code changes
    $effect(() => {
        const fc = functionCodes.find(f => f.code === selectedFc);
        if (fc) {
            selectedDataType = fc.defaultDataType;
        }
    });

    // Filter data types based on function code
    let availableDataTypes = $derived(() => {
        if (selectedFc === '01' || selectedFc === '02') {
            return dataTypes.filter(dt => dt.value === 'bool');
        }
        return dataTypes;
    });

    const handleAdd = () => {
        const def = generatedDefinition();
        if (def && onAddRegister) {
            onAddRegister(def);
            // Reset form
            registerName = '';
            startAddress = startAddress + totalRegisters;
            quantity = 1;
        }
    };

    const isValid = $derived(registerName.trim().length > 0 && /^[a-zA-Z_][a-zA-Z0-9_]*$/.test(registerName.trim()));
</script>

<div class="register-wizard">
    <div class="wizard-form">
        <div class="form-row-inline">
            <div class="form-field fc-field">
                <label class="cds--label">Function Code</label>
                <cds-dropdown
                    size="sm"
                    style="position: relative; top: 0.5rem;"
                    value={selectedFc}
                    onchange={(e) => selectedFc = e.target.value}>
                    {#each functionCodes as fc}
                        <cds-dropdown-item value={fc.code}>{fc.label}</cds-dropdown-item>
                    {/each}
                </cds-dropdown>
            </div>

            <div class="form-field name-field">
                <label class="cds--label">Name</label>
                <cds-text-input
                    size="sm"
                    value={registerName}
                    placeholder="temperature"
                    oninput={(e) => registerName = e.target.value}
                    invalid={registerName.length > 0 && !isValid}
                    invalid-text="Invalid name">
                </cds-text-input>
            </div>

            <div class="form-field address-field">
                <label class="cds--label">Address</label>
                <cds-number-input
                    size="sm"
                    value={startAddress}
                    min="0"
                    max="65535"
                    hide-steppers
                    oninput={(e) => startAddress = parseInt(e.target.value) || 0}>
                </cds-number-input>
            </div>

            <div class="form-field datatype-field">
                <label class="cds--label">Data Type</label>
                <cds-dropdown
                    size="sm"
                    style="position: relative; top: 0.5rem;"
                    value={selectedDataType}
                    onchange={(e) => selectedDataType = e.target.value}>
                    {#each availableDataTypes() as dt}
                        <cds-dropdown-item value={dt.value}>{dt.label}</cds-dropdown-item>
                    {/each}
                </cds-dropdown>
            </div>

            <div class="form-field qty-field">
                <label class="cds--label">Qty</label>
                <cds-number-input
                    size="sm"
                    value={quantity}
                    min="1"
                    max="125"
                    hide-steppers
                    oninput={(e) => quantity = parseInt(e.target.value) || 1}>
                </cds-number-input>
            </div>

            <div class="form-field btn-field">
                <label class="cds--label">&nbsp;</label>
                <cds-button
                    kind="primary"
                    size="sm"
                    disabled={!isValid}
                    onclick={handleAdd}>
                    + Add
                </cds-button>
            </div>
        </div>

        {#if generatedDefinition()}
            <div class="preview-section">
                <code class="preview-code">{generatedDefinition()}</code>
                <span class="preview-info">{addressPreview()} | {totalRegisters} reg.</span>
            </div>
        {/if}
    </div>
</div>

<style>
    .register-wizard {
        border: 1px solid var(--cds-border-subtle-01, #e0e0e0);
        border-radius: 4px;
        background: var(--cds-layer-01, #f4f4f4);
        margin-bottom: 0.75rem;
    }

    .wizard-form {
        padding: 0.75rem;
        padding-bottom: 1rem;
        display: flex;
        flex-direction: column;
        gap: 0.5rem;
    }

    .form-row-inline {
        display: flex;
        gap: 0.5rem;
        align-items: flex-end;
        flex-wrap: wrap;
    }

    .form-field {
        display: flex;
        flex-direction: column;
        gap: 0.125rem;
    }

    .form-field :global(cds-text-input),
    .form-field :global(cds-number-input),
    .form-field :global(cds-button) {
        height: 2rem;
    }

    .fc-field {
        flex: 0 0 200px;
    }

    .name-field {
        flex: 1;
        min-width: 120px;
    }

    .address-field {
        flex: 0 0 80px;
    }

    .datatype-field {
        flex: 0 0 160px;
    }

    .qty-field {
        flex: 0 0 60px;
    }

    .btn-field {
        flex: 0 0 auto;
    }

    .preview-section {
        display: flex;
        align-items: center;
        gap: 0.75rem;
        padding: 0.375rem 0.5rem;
        background: var(--cds-layer-02, #ffffff);
        border-radius: 4px;
        border: 1px solid var(--cds-border-subtle-01, #e0e0e0);
    }

    .preview-code {
        font-family: 'IBM Plex Mono', monospace;
        font-size: 0.75rem;
        color: var(--cds-text-primary, #161616);
        background: var(--cds-field-01, #f4f4f4);
        padding: 0.25rem 0.5rem;
        border-radius: 2px;
    }

    .preview-info {
        font-size: 0.6875rem;
        color: var(--cds-text-secondary, #525252);
    }

    .cds--label {
        font-size: 0.6875rem;
        color: var(--cds-text-secondary, #525252);
        margin-bottom: 0;
    }
</style>
