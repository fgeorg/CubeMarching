#ifndef SDF_STACK_HLSL
    #define SDF_STACK_HLSL

    // Metal does not support dynamic indexing into temporary arrays (e.g. float stack[16]; stack[i]),
    // so the stack is stored as named scalar fields with switch-based accessors to work around this.
    #define STACK_SIZE 16
    struct SdfStack
    {
        float s0;
        float s1;
        float s2;
        float s3;
        float s4;
        float s5;
        float s6;
        float s7;
        float s8;
        float s9;
        float s10;
        float s11;
        float s12;
        float s13;
        float s14;
        float s15;
    };

    void SetStackValue(inout SdfStack stack, int index, float val)
    {
        switch(index)
        {
            case 0: stack.s0 = val; break;
            case 1: stack.s1 = val; break;
            case 2: stack.s2 = val; break;
            case 3: stack.s3 = val; break;
            case 4: stack.s4 = val; break;
            case 5: stack.s5 = val; break;
            case 6: stack.s6 = val; break;
            case 7: stack.s7 = val; break;
            case 8: stack.s8 = val; break;
            case 9: stack.s9 = val; break;
            case 10: stack.s10 = val; break;
            case 11: stack.s11 = val; break;
            case 12: stack.s12 = val; break;
            case 13: stack.s13 = val; break;
            case 14: stack.s14 = val; break;
            case 15: stack.s15 = val; break;
        }
    }

    float GetStackValue(SdfStack stack, int index)
    {
        switch(index)
        {
            case 0: return stack.s0;
            case 1: return stack.s1;
            case 2: return stack.s2;
            case 3: return stack.s3;
            case 4: return stack.s4;
            case 5: return stack.s5;
            case 6: return stack.s6;
            case 7: return stack.s7;
            case 8: return stack.s8;
            case 9: return stack.s9;
            case 10: return stack.s10;
            case 11: return stack.s11;
            case 12: return stack.s12;
            case 13: return stack.s13;
            case 14: return stack.s14;
            case 15: return stack.s15;
        }
        return 1e10; // Should not be reached
    }

#endif
