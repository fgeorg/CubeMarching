#ifndef SDF_STACK_HLSL
#define SDF_STACK_HLSL

// Unity's HLSL→Metal compilation does not support dynamic indexing into temporary arrays
// (e.g. float stack[8]; stack[i]), so the stack is stored as named scalar fields with
// switch-based accessors to work around this.
#define STACK_SIZE 8
struct SdfStack {
    float s0;
    float s1;
    float s2;
    float s3;
    float s4;
    float s5;
    float s6;
    float s7;
};

void SetStackValue(inout SdfStack stack, int index, float val) {
    switch(index) {
        case 0: stack.s0 = val; break;
        case 1: stack.s1 = val; break;
        case 2: stack.s2 = val; break;
        case 3: stack.s3 = val; break;
        case 4: stack.s4 = val; break;
        case 5: stack.s5 = val; break;
        case 6: stack.s6 = val; break;
        case 7: stack.s7 = val; break;
    }
}

float GetStackValue(SdfStack stack, int index) {
    switch(index) {
        case 0: return stack.s0;
        case 1: return stack.s1;
        case 2: return stack.s2;
        case 3: return stack.s3;
        case 4: return stack.s4;
        case 5: return stack.s5;
        case 6: return stack.s6;
        case 7: return stack.s7;
    }
    return 1e10; // Should not be reached
}

struct SdfMaterial {
    float4 color; // rgba
    float metallic;
    float smoothness;
};

// Parallel material stack — same struct-with-switch pattern, holds SdfMaterial per slot.
struct SdfMaterialStack {
    SdfMaterial s0;
    SdfMaterial s1;
    SdfMaterial s2;
    SdfMaterial s3;
    SdfMaterial s4;
    SdfMaterial s5;
    SdfMaterial s6;
    SdfMaterial s7;
};

void SetMaterialStackValue(inout SdfMaterialStack stack, int index, SdfMaterial val) {
    switch(index) {
        case 0: stack.s0 = val; break;
        case 1: stack.s1 = val; break;
        case 2: stack.s2 = val; break;
        case 3: stack.s3 = val; break;
        case 4: stack.s4 = val; break;
        case 5: stack.s5 = val; break;
        case 6: stack.s6 = val; break;
        case 7: stack.s7 = val; break;
    }
}

SdfMaterial GetMaterialStackValue(SdfMaterialStack stack, int index) {
    switch(index) {
        case 0: return stack.s0;
        case 1: return stack.s1;
        case 2: return stack.s2;
        case 3: return stack.s3;
        case 4: return stack.s4;
        case 5: return stack.s5;
        case 6: return stack.s6;
        case 7: return stack.s7;
    }
    SdfMaterial def = (SdfMaterial)0; // Should not be reached
    return def;
}

#endif
