#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <stdarg.h>
#include "iec61850_server.h"
#include "iec61850_dynamic_model.h"
#include "iec61850_cdc.h"
#include "iec61850_client.h"
#include "mms_client_connection.h"
#include "hal_socket.h"
#include "hal_thread.h"
#include "hal_time.h"
#include "byte_buffer.h"

static int discover(MmsConnection client, int fixedModel)
{
    MmsError error;
    LinkedList domains = MmsConnection_getDomainNames(client, &error);
    int ok = error == MMS_ERROR_NONE && domains && LinkedList_size(domains) == 1 &&
        strcmp((char*)LinkedList_getData(LinkedList_getNext(domains)), "NFTESTLD0") == 0;
    if (domains) LinkedList_destroy(domains);
    LinkedList names = MmsConnection_getDomainVariableNames(client, &error, "NFTESTLD0");
    ok = ok && error == MMS_ERROR_NONE && names && (!fixedModel || LinkedList_size(names) == fixedModel);
    const char* items[] = { "GGIO1$ST$Status$stVal", "GGIO1$ST$Count$stVal", "GGIO1$MX$Analog$mag" };
    MmsType types[] = { MMS_BOOLEAN, MMS_INTEGER, MMS_FLOAT };
    for (int i = 0; i < 3; i++) {
        int found = 0;
        if (names)
            for (LinkedList entry = LinkedList_getNext(names); entry; entry = LinkedList_getNext(entry))
                if (strcmp((char*)LinkedList_getData(entry), items[i]) == 0) found = 1;
        ok = ok && found;
        MmsVariableSpecification* spec = MmsConnection_getVariableAccessAttributes(client, &error, "NFTESTLD0", items[i]);
        ok = ok && error == MMS_ERROR_NONE && spec && MmsVariableSpecification_getType(spec) == types[i];
        if (spec) MmsVariableSpecification_destroy(spec);
    }
    if (names) LinkedList_destroy(names);
    if (!fixedModel) {
        MmsVariableSpecification* parent = MmsConnection_getVariableAccessAttributes(client, &error, "NFTESTLD0", "GGIO1$ST$Status");
        ok = ok && error == MMS_ERROR_NONE && parent && MmsVariableSpecification_getType(parent) == MMS_STRUCTURE;
        if (parent) MmsVariableSpecification_destroy(parent);
        MmsValue* value = MmsConnection_readVariable(client, &error, "NFTESTLD0", "GGIO1$ST$Status");
        ok = ok && error == MMS_ERROR_NONE && value && MmsValue_getType(value) == MMS_STRUCTURE;
        if (value) MmsValue_delete(value);
    }
    MmsVariableSpecification* missing = MmsConnection_getVariableAccessAttributes(client, &error, "NFTESTLD0", "missing");
    ok = ok && error != MMS_ERROR_NONE && missing == NULL;
    if (missing) MmsVariableSpecification_destroy(missing);
    if (!ok) fprintf(stderr, "discovery mismatch\n");
    return ok;
}

static int silentServer(const char* address, int port)
{
    ServerSocket listener = TcpServerSocket_create(address, port);
    if (!listener) { fprintf(stderr, "silent listen failed\n"); return 1; }
    ServerSocket_listen(listener);
    puts("READY");
    fflush(stdout);
    Socket connection = NULL;
    uint64_t deadline = Hal_getTimeInMs() + 30000;
    while (!connection && Hal_getTimeInMs() < deadline) {
        connection = ServerSocket_accept(listener);
        if (!connection) Thread_sleep(10);
    }

    if (!connection) {
        fprintf(stderr, "silent accept timeout\n");
        ServerSocket_destroy(listener);
        return 1;
    }
    uint8_t cr[22];
    int used = 0;
    deadline = Hal_getTimeInMs() + 10000;
    while (used < sizeof(cr) && Hal_getTimeInMs() < deadline) {
        int n = Socket_read(connection, cr + used, (int)sizeof(cr) - used);
        if (n < 0) break;
        used += n;
        if (!n) Thread_sleep(10);
    }
    int ok = used == sizeof(cr) && cr[0] == 3 && cr[1] == 0 && cr[2] == 0 &&
        cr[3] == 22 && cr[4] == 17 && cr[5] == 0xe0;
    if (ok) {
        printf("SILENT CR ");
        for (int i = 0; i < used; i++) printf("%02X", cr[i]);
        puts("");
        fflush(stdout);
        char line[32];
        while (fgets(line, sizeof(line), stdin)) {
            if (strncmp(line, "quit", 4) == 0) break;
        }
    }
    else fprintf(stderr, "silent peer did not receive COTP CR\n");
    Socket_destroy(connection);
    ServerSocket_destroy(listener);
    return ok ? 0 : 1;
}

static MmsDataAccessError allowScalarWrite(DataAttribute* attribute, MmsValue* value, ClientConnection connection, void* parameter)
{
    (void)attribute; (void)connection; (void)parameter;
    if (MmsValue_getType(value) == MMS_INTEGER &&
        (MmsValue_toInt64(value) < -1000000 || MmsValue_toInt64(value) > 1000000))
        return DATA_ACCESS_ERROR_OBJECT_VALUE_INVALID;
    return DATA_ACCESS_ERROR_SUCCESS;
}

static ControlHandlerResult memoryControl(ControlAction action, void* parameter, MmsValue* value, bool test)
{
    (void)action;
    if (!test) {
        IedServer server = (IedServer)parameter;
        ModelNode* object = (ModelNode*)ControlAction_getControlObject(action);
        DataAttribute* state = (DataAttribute*)ModelNode_getChild(object, "stVal");
        IedServer_updateBooleanAttributeValue(server, state, MmsValue_getBoolean(value));
    }
    return CONTROL_RESULT_OK;
}

static ControlHandlerResult enhancedControl(ControlAction action, void* parameter, MmsValue* value, bool test)
{
    if (ControlAction_getOrCat(action) == 8 || ControlAction_getOrCat(action) == 7) {
        ControlAction_setError(action, CONTROL_ERROR_UNKNOWN);
        ControlAction_setAddCause(action, ControlAction_getOrCat(action) == 8 ?
            ADD_CAUSE_BLOCKED_BY_PROCESS : ADD_CAUSE_TIME_LIMIT_OVER);
        return CONTROL_RESULT_FAILED;
    }
    return memoryControl(action, parameter, value, test);
}

static void terminated(void* parameter, ControlObjectClient control)
{
    LastApplError detail = ControlObjectClient_getLastApplError(control);
    *((volatile int*)parameter) = detail.addCause == ADD_CAUSE_UNKNOWN ? 1 :
        detail.addCause == ADD_CAUSE_BLOCKED_BY_PROCESS ? 2 :
        detail.addCause == ADD_CAUSE_TIME_LIMIT_OVER ? 3 : -1;
}

static int enhancedActions(IedConnection connection, bool sbo)
{
    const char* reference = sbo ? "NFTESTLD0/GGIO1.SboEnhanced" : "NFTESTLD0/GGIO1.Enhanced";
    ControlObjectClient control = ControlObjectClient_create(reference, connection);
    if (!control) { fprintf(stderr, "Enhanced control create failed\n"); return 0; }
    volatile int completion = 0;
    ControlObjectClient_setCommandTerminationHandler(control, terminated, (void*)&completion);
    int ok = 1;
    for (int i = 0; i < 4 && ok; i++) {
        completion = 0;
        ControlObjectClient_setTestMode(control, i == 3);
        ControlObjectClient_setOrigin(control, "native-enhanced", i == 0 || i == 3 ? 3 : i == 1 ? 8 : 7);
        MmsValue* value = MmsValue_newBoolean(i == 0);
        if (sbo) ok = ControlObjectClient_selectWithValue(control, value);
        if (ok) ok = ControlObjectClient_operate(control, value, 0);
        MmsValue_delete(value);
        uint64_t deadline = Hal_getTimeInMs() + 5000;
        while (completion == 0 && Hal_getTimeInMs() < deadline) Thread_sleep(10);
        ok = ok && completion == (i == 3 ? 1 : i + 1);
        IedClientError error;
        bool actual = IedConnection_readBooleanValue(connection, &error,
            sbo ? "NFTESTLD0/GGIO1.SboEnhanced.stVal" : "NFTESTLD0/GGIO1.Enhanced.stVal", IEC61850_FC_ST);
        ok = ok && error == IED_ERROR_OK && actual;
        if (!ok) fprintf(stderr, "Enhanced completion i=%i result=%i error=%i\n",
            i, completion, ControlObjectClient_getLastError(control));
    }
    if (sbo && ok) {
        ControlObjectClient_setTestMode(control, false);
        ControlObjectClient_setOrigin(control, "native-enhanced", 3);
        MmsValue* value = MmsValue_newBoolean(false);
        ok = ControlObjectClient_selectWithValue(control, value) && ControlObjectClient_cancel(control);
        if (ok) {
            ok = ControlObjectClient_selectWithValue(control, value);
            Thread_sleep(1200);
            if (ok) ok = !ControlObjectClient_operate(control, value, 0);
            LastApplError detail = ControlObjectClient_getLastApplError(control);
            ok = ok && detail.addCause == ADD_CAUSE_OBJECT_NOT_SELECTED;
        }
        MmsValue_delete(value);
    }
    ControlObjectClient_destroy(control);
    if (ok) puts(sbo ? "NATIVE SBO ENHANCED PASS" : "NATIVE ENHANCED PASS");
    return ok;
}
static ControlHandlerResult typedControl(ControlAction action, void* parameter, MmsValue* value, bool test)
{
    if (!test) {
        ModelNode* object = (ModelNode*)ControlAction_getControlObject(action);
        IedServer server = (IedServer)parameter;
        if (object->name[0] == 'D')
            IedServer_updateDbposValue(server, (DataAttribute*)ModelNode_getChild(object, "stVal"),
                MmsValue_getBoolean(value) ? DBPOS_ON : DBPOS_OFF);
        else {
            MmsValue* real = MmsValue_getType(value) == MMS_STRUCTURE ? MmsValue_getElement(value, 0) : value;
            IedServer_updateFloatAttributeValue(server, (DataAttribute*)ModelNode_getChild(object, "mxVal.f"),
                MmsValue_toFloat(real));
        }
    }
    return CONTROL_RESULT_OK;
}
static int typedActions(IedConnection connection)
{
    for (int kind = 1; kind <= 2; kind++)
    for (int mode = 1; mode <= 4; mode++) {
        char reference[64], state[80];
        snprintf(reference, sizeof(reference), "NFTESTLD0/GGIO1.%c%i", kind == 1 ? 'D' : 'A', mode);
        snprintf(state, sizeof(state), "%s.%s", reference, kind == 1 ? "stVal" : "mxVal.f");
        ControlObjectClient control = ControlObjectClient_create(reference, connection);
        if (!control) { fprintf(stderr, "typed create %s failed\n", reference); return 0; }
        int ok = ControlObjectClient_getControlModel(control) == mode;
        volatile int completion = 0;
        ControlObjectClient_setCommandTerminationHandler(control, terminated, (void*)&completion);
        ControlObjectClient_setOrigin(control, "native-typed", 3);
        for (int action = 0; action < 3 && ok; action++) {
            bool test = action == 2;
            ControlObjectClient_setTestMode(control, test); completion = 0;
            MmsValue* value = kind == 1 ? MmsValue_newBoolean(action != 1) : MmsValue_newFloat(action == 1 ? -6.25f : 12.5f);
            if (mode == 2) ok = ControlObjectClient_select(control);
            if (mode == 4) ok = ControlObjectClient_selectWithValue(control, value);
            if (ok) ok = ControlObjectClient_operate(control, value, 0);
            MmsValue_delete(value);
            if (mode >= 3 && ok) {
                uint64_t deadline = Hal_getTimeInMs() + 5000;
                while (!completion && Hal_getTimeInMs() < deadline) Thread_sleep(10);
                ok = completion == 1;
            }
            IedClientError error;
            MmsValue* actual = IedConnection_readObject(connection, &error, state, kind == 1 ? IEC61850_FC_ST : IEC61850_FC_MX);
            ok = ok && error == IED_ERROR_OK && actual &&
                (kind == 1 ? MmsValue_getType(actual) == MMS_BIT_STRING &&
                    MmsValue_getBitStringAsIntegerBigEndian(actual) == (action == 0 ? 2 : 1) :
                    MmsValue_getType(actual) == MMS_FLOAT && MmsValue_toFloat(actual) == (action == 0 ? 12.5f : -6.25f));
            if (actual) MmsValue_delete(actual);
            if (!ok) fprintf(stderr, "typed operation %s action=%i error=%i completion=%i\n",
                reference, action, ControlObjectClient_getLastError(control), completion);
        }
        ControlObjectClient_destroy(control);
        if (!ok) return 0;
    }
    puts("NATIVE TYPED PASS");
    return 1;
}
static void dataReport(void* parameter, ClientReport report)
{
        MmsValue* values = ClientReport_getDataSetValues(report);
        int ok = values && MmsValue_getArraySize(values) == 3 &&
            MmsValue_getBoolean(MmsValue_getElement(values, 0)) &&
            MmsValue_toInt32(MmsValue_getElement(values, 1)) == -123456 &&
            MmsValue_toFloat(MmsValue_getElement(values, 2)) == 12.5f;
        *((volatile int*)parameter) = ok ? 1 : -1;
}
static int dataActions(IedConnection connection)
{
        MmsConnection mms = IedConnection_getMmsConnection(connection);
        MmsError error;
        LinkedList items = LinkedList_create();
        LinkedList_add(items, "GGIO1$ST$Status$stVal");
        LinkedList_add(items, "GGIO1$ST$Count$stVal");
        LinkedList_add(items, "GGIO1$MX$Analog$mag");
        MmsValue* values = MmsConnection_readMultipleVariables(mms, &error, "NFTESTLD0", items);
        int ok = error == MMS_ERROR_NONE && values && MmsValue_getArraySize(values) == 3;
        if (values) MmsValue_delete(values);
        LinkedList_destroyStatic(items);
        bool deletable = true;
        LinkedList directory = MmsConnection_readNamedVariableListDirectory(mms, &error, "NFTESTLD0", "GGIO1$Measurements", &deletable);
        ok = ok && error == MMS_ERROR_NONE && directory && LinkedList_size(directory) == 3 && !deletable;
        if (directory) LinkedList_destroyDeep(directory, (LinkedListValueDeleteFunction)MmsVariableAccessSpecification_destroy);
        values = MmsConnection_readNamedVariableListValues(mms, &error, "NFTESTLD0", "GGIO1$Measurements", true);
        ok = ok && error == MMS_ERROR_NONE && values && MmsValue_getArraySize(values) == 3;
        if (values) MmsValue_delete(values);
        IedClientError iedError;
        ClientReportControlBlock rcb = IedConnection_getRCBValues(connection, &iedError, "NFTESTLD0/GGIO1.RP.Events", NULL);
        ok = ok && iedError == IED_ERROR_OK && rcb;
        volatile int received = 0;
        if (ok) {
            IedConnection_installReportHandler(connection, "NFTESTLD0/GGIO1.RP.Events",
                ClientReportControlBlock_getRptId(rcb), dataReport, (void*)&received);
            ClientReportControlBlock_setRptEna(rcb, true);
            IedConnection_setRCBValues(connection, &iedError, rcb, RCB_ELEMENT_RPT_ENA, false);
            ok = iedError == IED_ERROR_OK;
            ClientReportControlBlock_setGI(rcb, true);
            IedConnection_setRCBValues(connection, &iedError, rcb, RCB_ELEMENT_GI, false);
            ok = ok && iedError == IED_ERROR_OK;
            uint64_t deadline = Hal_getTimeInMs() + 5000;
            while (received == 0 && Hal_getTimeInMs() < deadline) Thread_sleep(10);
            ok = ok && received == 1;
            ClientReportControlBlock_setRptEna(rcb, false);
            IedConnection_setRCBValues(connection, &iedError, rcb, RCB_ELEMENT_RPT_ENA, false);
            ok = ok && iedError == IED_ERROR_OK;
            IedConnection_uninstallReportHandler(connection, "NFTESTLD0/GGIO1.RP.Events");
        }
        if (rcb) ClientReportControlBlock_destroy(rcb);
        values = MmsConnection_readVariable(mms, &error, "NFTESTLD0", "GGIO1$ST$Status$q");
        ok = ok && error == MMS_ERROR_NONE && values && MmsValue_getType(values) == MMS_BIT_STRING &&
            Quality_fromMmsValue(values) == (QUALITY_VALIDITY_INVALID | QUALITY_TEST | QUALITY_DETAIL_OLD_DATA);
        if (values) MmsValue_delete(values);
        values = MmsConnection_readVariable(mms, &error, "NFTESTLD0", "GGIO1$ST$Status$t");
        ok = ok && error == MMS_ERROR_NONE && values && MmsValue_getType(values) == MMS_UTC_TIME &&
            MmsValue_getUtcTimeInMs(values) == 1700000000125ULL && MmsValue_getUtcTimeQuality(values) == 0x3f;
        if (values) MmsValue_delete(values);
        if (!ok) fprintf(stderr, "Data/report failure mms=%i ied=%i report=%i\n", error, iedError, received);
        else puts("NATIVE DATA REPORT PASS");
        return ok;
}
static int sboEnhancedStress(IedConnection connection, int repetitions)
{
    ControlObjectClient control = ControlObjectClient_create("NFTESTLD0/GGIO1.SboEnhanced", connection);
    if (!control) { fprintf(stderr, "SBO enhanced stress create failed\n"); return 0; }
    volatile int completion = 0;
    ControlObjectClient_setCommandTerminationHandler(control, terminated, (void*)&completion);
    ControlObjectClient_setOrigin(control, "native-sbo-stress", 3);
    int ok = 1;
    for (int i = 0; i < repetitions && ok; i++) {
        completion = 0;
        MmsValue* value = MmsValue_newBoolean((i & 1) != 0);
        ok = ControlObjectClient_selectWithValue(control, value) && ControlObjectClient_operate(control, value, 0);
        MmsValue_delete(value);
        uint64_t deadline = Hal_getTimeInMs() + 5000;
        while (completion == 0 && Hal_getTimeInMs() < deadline) Thread_sleep(1);
        IedClientError error;
        bool actual = IedConnection_readBooleanValue(connection, &error, "NFTESTLD0/GGIO1.SboEnhanced.stVal", IEC61850_FC_ST);
        ok = ok && completion == 1 && error == IED_ERROR_OK && actual == ((i & 1) != 0);
        if (!ok) fprintf(stderr, "SBO enhanced stress i=%i completion=%i error=%i\n",
            i, completion, ControlObjectClient_getLastError(control));
    }
    ControlObjectClient_destroy(control);
    if (ok) printf("NATIVE SBO ENHANCED STRESS PASS repetitions=%i\n", repetitions);
    return ok;
}
static int controlActions(IedConnection connection, bool expire)
{
    int ok = 1;
    const char* references[] = { "NFTESTLD0/GGIO1.Direct", "NFTESTLD0/GGIO1.Selected" };
    for (int i = 0; i < 2 && ok; i++) {
        ControlObjectClient control = ControlObjectClient_create(references[i], connection);
        if (!control) { fprintf(stderr, "control create failed %s\n", references[i]); ok = 0; break; }
        ControlObjectClient_setOrigin(control, "native-memory", 3);
        if (i == 1) ok = ControlObjectClient_select(control);
        MmsValue* value = MmsValue_newBoolean(true);
        if (ok) ok = ControlObjectClient_operate(control, value, 0);
        MmsValue_delete(value);
        if (ok) {
            IedClientError error;
            char state[128];
            snprintf(state, sizeof(state), "%s.stVal", references[i]);
            bool actual = IedConnection_readBooleanValue(connection, &error, state, IEC61850_FC_ST);
            ok = error == IED_ERROR_OK && actual;
        }
        if (i == 1 && ok) {
            ok = ControlObjectClient_select(control);
            if (ok) ok = ControlObjectClient_cancel(control);
            if (ok && expire) {
                ok = ControlObjectClient_select(control);
                Thread_sleep(1200);
                MmsValue* rejected = MmsValue_newBoolean(false);
                if (ok) ok = !ControlObjectClient_operate(control, rejected, 0);
                MmsValue_delete(rejected);
                LastApplError detail = ControlObjectClient_getLastApplError(control);
                ok = ok && detail.error == CONTROL_ERROR_NO_ERROR && detail.addCause == ADD_CAUSE_OBJECT_NOT_SELECTED;
                IedClientError error;
                bool actual = IedConnection_readBooleanValue(connection, &error, "NFTESTLD0/GGIO1.Selected.stVal", IEC61850_FC_ST);
                ok = ok && error == IED_ERROR_OK && actual;
            }
        }
        if (!ok) fprintf(stderr, "control action failed %s error=%i\n", references[i], ControlObjectClient_getLastError(control));
        ControlObjectClient_destroy(control);
    }
    if (ok) puts("NATIVE CONTROL PASS");
    return ok;
}

static int controlStress(IedConnection connection, int repetitions)
{
    int ok = 1;
    const char* references[] = { "NFTESTLD0/GGIO1.Direct", "NFTESTLD0/GGIO1.Selected" };
    for (int object = 0; object < 2 && ok; object++) {
        ControlObjectClient control = ControlObjectClient_create(references[object], connection);
        if (!control) return 0;
        ControlObjectClient_setOrigin(control, "native-stress", 3);
        for (int i = 0; i < repetitions && ok; i++) {
            bool requested = (i & 1) != 0;
            if (object == 1) ok = ControlObjectClient_select(control);
            MmsValue* value = MmsValue_newBoolean(requested);
            if (ok) ok = ControlObjectClient_operate(control, value, 0);
            MmsValue_delete(value);
            IedClientError error;
            char state[128];
            snprintf(state, sizeof(state), "%s.stVal", references[object]);
            bool actual = IedConnection_readBooleanValue(connection, &error, state, IEC61850_FC_ST);
            ok = ok && error == IED_ERROR_OK && actual == requested;
        }
        for (int failure = 0; failure < 2 && ok; failure++) {
            ControlObjectClient_setTestMode(control, failure == 0);
            ControlObjectClient_setOrigin(control, "native-stress", failure == 0 ? 3 : 8);
            if (object == 1) ok = ControlObjectClient_select(control);
            MmsValue* value = MmsValue_newBoolean(false);
            bool accepted = ok && ControlObjectClient_operate(control, value, 0);
            MmsValue_delete(value);
            ok = ok && accepted == (failure == 0);
            if (ok && failure == 1) {
                LastApplError detail = ControlObjectClient_getLastApplError(control);
                ok = detail.error == CONTROL_ERROR_UNKNOWN && detail.addCause == ADD_CAUSE_UNKNOWN;
            }
            IedClientError error;
            char state[128];
            snprintf(state, sizeof(state), "%s.stVal", references[object]);
            bool actual = IedConnection_readBooleanValue(connection, &error, state, IEC61850_FC_ST);
            ok = ok && error == IED_ERROR_OK && actual;
        }
        ControlObjectClient_destroy(control);
    }
    if (ok) printf("NATIVE CONTROL STRESS PASS repetitions=%i operations=%i\n", repetitions, repetitions * 2);
    else fprintf(stderr, "Native control stress failed\n");
    return ok;
}

static int verifyWrites(MmsConnection client)
{
    const char* items[] = { "GGIO1$ST$Status$stVal", "GGIO1$ST$Count$stVal", "GGIO1$MX$Analog$mag" };
    MmsValue* values[] = { MmsValue_newBoolean(false), MmsValue_newIntegerFromInt32(42), MmsValue_newFloat(9.25f) };
    MmsError error;
    int ok = 1;
    for (int i = 0; i < 3; i++) {
        MmsValue* original = MmsConnection_readVariable(client, &error, "NFTESTLD0", items[i]);
        ok = ok && error == MMS_ERROR_NONE && original;
        MmsDataAccessError result = MmsConnection_writeVariable(client, &error, "NFTESTLD0", items[i], values[i]);
        if (error != MMS_ERROR_NONE || result != DATA_ACCESS_ERROR_SUCCESS)
            fprintf(stderr, "Write %s failed: error=%i result=%i\n", items[i], error, result);
        ok = ok && error == MMS_ERROR_NONE && result == DATA_ACCESS_ERROR_SUCCESS;
        MmsValue* actual = MmsConnection_readVariable(client, &error, "NFTESTLD0", items[i]);
        int equal = actual && (i == 0 ? MmsValue_getBoolean(actual) == false :
            i == 1 ? MmsValue_toInt32(actual) == 42 : MmsValue_toFloat(actual) == 9.25f);
        if (!equal) fprintf(stderr, "Readback mismatch %s\n", items[i]);
        ok = ok && error == MMS_ERROR_NONE && equal;
        if (actual) MmsValue_delete(actual);
        if (original) {
            result = MmsConnection_writeVariable(client, &error, "NFTESTLD0", items[i], original);
            ok = ok && error == MMS_ERROR_NONE && result == DATA_ACCESS_ERROR_SUCCESS;
            MmsValue_delete(original);
        }
    }
    MmsDataAccessError denied = MmsConnection_writeVariable(client, &error, "NFTESTLD0", "bundle", values[0]);
    ok = ok && error == MMS_ERROR_ACCESS_OBJECT_ACCESS_DENIED && denied == DATA_ACCESS_ERROR_OBJECT_ACCESS_DENIED;
    denied = MmsConnection_writeVariable(client, &error, "NFTESTLD0", items[1], values[0]);
    ok = ok && error == MMS_ERROR_DEFINITION_TYPE_INCONSISTENT && denied == DATA_ACCESS_ERROR_TYPE_INCONSISTENT;
    denied = MmsConnection_writeVariable(client, &error, "NFTESTLD0", "missing", values[0]);
    ok = ok && error == MMS_ERROR_ACCESS_OBJECT_NON_EXISTENT && denied == DATA_ACCESS_ERROR_OBJECT_NONE_EXISTENT;
    MmsValue_setInt32(values[1], 1000001);
    denied = MmsConnection_writeVariable(client, &error, "NFTESTLD0", items[1], values[1]);
    ok = ok && error == MMS_ERROR_ACCESS_OBJECT_VALUE_INVALID && denied == DATA_ACCESS_ERROR_OBJECT_VALUE_INVALID;
    for (int i = 0; i < 3; i++) MmsValue_delete(values[i]);
    if (!ok) fprintf(stderr, "Write verification failed\n");
    return ok;
}

static int verifyComposite(MmsConnection client)
{
    MmsError error;
    MmsVariableSpecification* spec = MmsConnection_getVariableAccessAttributes(client, &error, "NFTESTLD0", "bundle");
    int ok = error == MMS_ERROR_NONE && spec && MmsVariableSpecification_getType(spec) == MMS_STRUCTURE &&
        MmsVariableSpecification_getSize(spec) == 2;
    if (ok) {
        MmsVariableSpecification* array = MmsVariableSpecification_getChildSpecificationByIndex(spec, 1);
        ok = strcmp(MmsVariableSpecification_getName(array), "samples") == 0 &&
            MmsVariableSpecification_getType(array) == MMS_ARRAY && MmsVariableSpecification_getSize(array) == 3 &&
            MmsVariableSpecification_getType(MmsVariableSpecification_getArrayElementSpecification(array)) == MMS_INTEGER;
    }
    if (spec) MmsVariableSpecification_destroy(spec);
    for (int round = 0; round < 3 && ok; round++) {
        MmsValue* value = MmsConnection_readVariable(client, &error, "NFTESTLD0", "bundle");
        ok = error == MMS_ERROR_NONE && value && MmsValue_getType(value) == MMS_STRUCTURE &&
            MmsValue_getArraySize(value) == 2 && MmsValue_getBoolean(MmsValue_getElement(value, 0));
        if (ok) {
            MmsValue* array = MmsValue_getElement(value, 1);
            ok = MmsValue_getType(array) == MMS_ARRAY && MmsValue_getArraySize(array) == 3;
            for (int i = 0; i < 3 && ok; i++) ok = MmsValue_toInt32(MmsValue_getElement(array, i)) == 100 + i;
        }
        if (value) MmsValue_delete(value);
    }
    return ok;
}

/* 생성 MMS/IEC 바인딩을 독립 libiec61850 Client/Server와 맞대는 시험 래퍼입니다.
 * IEC 보고는 라이브러리 API를 사용하지만 아래 MMS listOfVariables PDU는 래퍼가 직접 인코딩합니다. */
#define MAP_LD "NFTESTLD0"
#define MAP_STATUS "GGIO1$ST$Status$stVal"
#define MAP_QUALITY "GGIO1$ST$Status$q"
#define MAP_TIME "GGIO1$ST$Status$t"
#define MAP_POS "GGIO1$ST$Pos$stVal"
#define MAP_LEVEL "GGIO1$MX$Level$mag"
#define MAP_TARGET "GGIO1$SP$Target$setVal"
#define MAP_GAIN "GGIO1$SP$Gain$setVal"
#define MAP_COMMAND "GGIO1$SP$Command$setVal"
#define MAP_WRITES "GGIO1$ST$Writes$stVal"
#define MAP_UNMAPPED "GGIO1$ST$Unmapped$stVal"
#define MAP_INITIAL_QUALITY (QUALITY_VALIDITY_INVALID | QUALITY_TEST | QUALITY_DETAIL_OLD_DATA)

/* 원본 정적 라이브러리의 내부 심벌이며 f0f797bf955da763353f7081c892a1ff6b55e5a1에 의존합니다.
 * v1.6.2의 MmsServerConnection_sendInformationReportListOfVariables는 domain 항목의 길이를
 * 래퍼 한 겹으로 계산하지만 30{a0{a1{...}}}로 인코딩하므로 여기서 PDU를 직접 구성합니다.
 * 이 합성 보고 경로를 외부 라이브러리 리포트 인코더 검증으로 계산하지 마십시오. */
void* private_ClientConnection_getServerConnectionHandle(ClientConnection self);
void MmsServerConnection_sendInformationReportVMDSpecific(MmsServerConnection self, char* itemId,
    LinkedList values, bool handlerMode);
ByteBuffer* MmsServer_reserveTransmitBuffer(MmsServer self);
void MmsServer_releaseTransmitBuffer(MmsServer self);
void IsoConnection_sendMessage(void* self, ByteBuffer* message);
typedef struct {
    int maxServOutstandingCalling, maxServOutstandingCalled, dataStructureNestingLevel;
    uint8_t negotiatedParameterCBC[2];
    uint32_t maxPduSize;
    void* isoConnection;
    MmsServer server;
} MapConnectionPrefix; /* leading fields of struct sMmsServerConnection at the pinned revision */
static IedServer mapServer;
static DataAttribute *mapStatus, *mapQuality, *mapTime, *mapPos, *mapLevel, *mapTarget, *mapGain, *mapCommand,
    *mapWrites, *mapUnmapped;
static volatile int mapAccepted, mapPending = -1, mapQuit, mapReportsSent, mapCommands;
static uint64_t mapEpoch;
/* Timestamped stderr trace (ms since READY) so a board failure can be matched to the native receive/send stage. */
static void mapTrace(const char* format, ...)
{
    va_list args;
    va_start(args, format);
    fprintf(stderr, "[%llu] ", (unsigned long long)(Hal_getTimeInMs() - mapEpoch));
    vfprintf(stderr, format, args);
    fputc('\n', stderr);
    fflush(stderr);
    va_end(args);
}

static void mapEntry(LinkedList specs, LinkedList values, MmsVariableAccessSpecification* spec,
    const char* domain, const char* item, MmsValue* value)
{
    spec->domainId = (char*)domain; spec->itemId = (char*)item; spec->arrayIndex = -1; spec->componentName = NULL;
    LinkedList_add(specs, spec); LinkedList_add(values, value);
}

static int mapWrap(uint8_t* destination, int position, uint8_t tag, const uint8_t* source, int length)
{
    destination[position++] = tag;
    if (length >= 128) destination[position++] = 0x81;
    destination[position++] = (uint8_t)length;
    memcpy(destination + position, source, length);
    return position + length;
}

static void mapSendListReport(MmsServerConnection mms, LinkedList specs, LinkedList values)
{
    uint8_t list[400], results[200], scratch[120], inner[120], content[620], pdu[640];
    int listSize = 0, resultSize = 0, contentSize = 0;
    for (LinkedList e = LinkedList_getNext(specs); e; e = LinkedList_getNext(e)) {
        MmsVariableAccessSpecification* spec = (MmsVariableAccessSpecification*)LinkedList_getData(e);
        int size = 0, name;
        if (spec->domainId) {
            size = mapWrap(scratch, 0, 0x1a, (const uint8_t*)spec->domainId, (int)strlen(spec->domainId));
            size = mapWrap(scratch, size, 0x1a, (const uint8_t*)spec->itemId, (int)strlen(spec->itemId));
            name = mapWrap(inner, 0, 0xa1, scratch, size);
        }
        else name = mapWrap(inner, 0, 0x80, (const uint8_t*)spec->itemId, (int)strlen(spec->itemId));
        size = mapWrap(scratch, 0, 0xa0, inner, name);
        listSize = mapWrap(list, listSize, 0x30, scratch, size);
    }
    for (LinkedList e = LinkedList_getNext(values); e; e = LinkedList_getNext(e))
        resultSize = MmsValue_encodeMmsData((MmsValue*)LinkedList_getData(e), results, resultSize, true);
    contentSize = mapWrap(content, 0, 0xa0, list, listSize);
    contentSize = mapWrap(content, contentSize, 0xa0, results, resultSize);
    int body = mapWrap(pdu, 0, 0xa0, content, contentSize);
    uint8_t report[660];
    int length = mapWrap(report, 0, 0xa3, pdu, body);
    MapConnectionPrefix* connection = (MapConnectionPrefix*)mms;
    ByteBuffer* buffer = MmsServer_reserveTransmitBuffer(connection->server);
    memcpy(buffer->buffer, report, length);
    buffer->size = length;
    IsoConnection_sendMessage(connection->isoConnection, buffer);
    MmsServer_releaseTransmitBuffer(connection->server);
    mapTrace("report bytes=%i handed to IsoConnection", length);
}

static void mapSendReport(ClientConnection connection, int command)
{
    MmsServerConnection mms = (MmsServerConnection)private_ClientConnection_getServerConnectionHandle(connection);
    LinkedList specs = LinkedList_create();
    LinkedList values = LinkedList_create();
    MmsVariableAccessSpecification entries[5];
    switch (command) {
    case 101: /* unmapped, VMD-scope, shuffled mapped entries: applied */
        mapEntry(specs, values, &entries[0], MAP_LD, MAP_UNMAPPED, MmsValue_newIntegerFromInt32(77));
        mapEntry(specs, values, &entries[1], NULL, "Vmd", MmsValue_newBoolean(true));
        mapEntry(specs, values, &entries[2], MAP_LD, MAP_STATUS, MmsValue_newBoolean(false));
        mapEntry(specs, values, &entries[3], MAP_LD, MAP_WRITES, MmsValue_newIntegerFromInt32(mapAccepted));
        mapEntry(specs, values, &entries[4], MAP_LD, MAP_LEVEL, MmsValue_newFloat(30.5f));
        break;
    case 102: /* valid first entry, invalid float later: rejected atomically */
        mapEntry(specs, values, &entries[0], MAP_LD, MAP_STATUS, MmsValue_newBoolean(true));
        mapEntry(specs, values, &entries[1], MAP_LD, MAP_LEVEL, MmsValue_newFloat(NAN));
        break;
    case 103: /* mapped access error after valid entries */
        mapEntry(specs, values, &entries[0], MAP_LD, MAP_LEVEL, MmsValue_newFloat(33.5f));
        mapEntry(specs, values, &entries[1], MAP_LD, MAP_UNMAPPED, MmsValue_newIntegerFromInt32(1));
        mapEntry(specs, values, &entries[2], MAP_LD, MAP_STATUS,
            MmsValue_newDataAccessError(DATA_ACCESS_ERROR_OBJECT_ACCESS_DENIED));
        break;
    case 104: /* mapped type mismatch */
        mapEntry(specs, values, &entries[0], MAP_LD, MAP_LEVEL, MmsValue_newFloat(34.5f));
        mapEntry(specs, values, &entries[1], MAP_LD, MAP_STATUS, MmsValue_newIntegerFromInt32(1));
        break;
    case 105: /* VMD named variable list without a domain directory */
        LinkedList_add(values, MmsValue_newBoolean(true));
        LinkedList_add(values, MmsValue_newFloat(35.5f));
        break;
    default: /* partial report */
        mapEntry(specs, values, &entries[0], MAP_LD, MAP_LEVEL, MmsValue_newFloat(32.5f));
        break;
    }
    if (command == 105) MmsServerConnection_sendInformationReportVMDSpecific(mms, "Vmd", values, true);
    else mapSendListReport(mms, specs, values);
    LinkedList_destroyDeep(values, (LinkedListValueDeleteFunction)MmsValue_delete);
    LinkedList_destroyStatic(specs);
    mapReportsSent++;
}
static MmsDataAccessError mapWrite(DataAttribute* attribute, MmsValue* value, ClientConnection connection, void* parameter)
{
    (void)parameter;
    if (attribute == mapGain) {
        if (MmsValue_getType(value) != MMS_FLOAT) return DATA_ACCESS_ERROR_TYPE_INCONSISTENT;
        float gain = MmsValue_toFloat(value);
        mapTrace("write Gain=%g received", (double)gain);
        return isnan(gain) || isinf(gain) ? DATA_ACCESS_ERROR_OBJECT_VALUE_INVALID : DATA_ACCESS_ERROR_SUCCESS;
    }
    if (MmsValue_getType(value) != MMS_INTEGER) return DATA_ACCESS_ERROR_TYPE_INCONSISTENT;
    int32_t number = MmsValue_toInt32(value);
    mapTrace("write %s=%i received", attribute == mapTarget ? "Target" : attribute == mapCommand ? "Command" : "other", (int)number);
    if (attribute == mapTarget) {
        if (number < 0 || number > 1000) return DATA_ACCESS_ERROR_OBJECT_VALUE_INVALID;
        mapAccepted++;
        if (number == 999) { Thread_sleep(6000); mapTrace("write Target=999 handler resumed after 6000 ms"); } /* response loss after the side effect */
        return DATA_ACCESS_ERROR_SUCCESS;
    }
    if (attribute == mapCommand) {
        mapCommands++;
        /* Applied by the main loop: write handlers run under the MMS model lock. */
        if (number >= 0 && number <= 3) { mapPending = number; return DATA_ACCESS_ERROR_SUCCESS; }
        if (number >= 101 && number <= 106) {
            mapSendReport(connection, number);
            mapTrace("write Command=%i handler returns; response follows", (int)number);
            return DATA_ACCESS_ERROR_SUCCESS;
        }
        return DATA_ACCESS_ERROR_OBJECT_VALUE_INVALID;
    }
    return DATA_ACCESS_ERROR_OBJECT_ACCESS_DENIED;
}

static void mapConnection(IedServer server, ClientConnection connection, bool connected, void* parameter)
{
    (void)server; (void)parameter;
    mapTrace("%s %s", connected ? "connected" : "disconnected", ClientConnection_getPeerAddress(connection));
}

static void mapSetTime(uint64_t milliseconds, uint8_t quality)
{
    MmsValue* utc = MmsValue_newUtcTimeByMsTime(milliseconds);
    MmsValue_setUtcTimeQuality(utc, quality);
    IedServer_updateAttributeValue(mapServer, mapTime, utc);
    MmsValue_delete(utc);
}

static void mapApply(int command)
{
    IedServer_lockDataModel(mapServer);
    if (command == 0) {
        IedServer_updateBooleanAttributeValue(mapServer, mapStatus, true);
        IedServer_updateQuality(mapServer, mapQuality, MAP_INITIAL_QUALITY);
        mapSetTime(1700000000125ULL, 0x3f);
        IedServer_updateDbposValue(mapServer, mapPos, DBPOS_ON);
        IedServer_updateFloatAttributeValue(mapServer, mapLevel, 12.5f);
        IedServer_updateInt32AttributeValue(mapServer, mapTarget, 0);
        IedServer_updateFloatAttributeValue(mapServer, mapGain, 1.0f);
        IedServer_updateInt32AttributeValue(mapServer, mapUnmapped, 0);
    }
    else if (command == 1) {
        IedServer_updateBooleanAttributeValue(mapServer, mapStatus, false);
        IedServer_updateQuality(mapServer, mapQuality, QUALITY_VALIDITY_GOOD);
        mapSetTime(1700000001000ULL, 0x0a);
        IedServer_updateDbposValue(mapServer, mapPos, DBPOS_OFF);
        IedServer_updateFloatAttributeValue(mapServer, mapLevel, 20.5f);
        IedServer_updateInt32AttributeValue(mapServer, mapUnmapped, 77);
    }
    else if (command == 2) {
        IedServer_updateFloatAttributeValue(mapServer, mapLevel, NAN);
        IedServer_updateBooleanAttributeValue(mapServer, mapStatus, true);
    }
    else IedServer_updateFloatAttributeValue(mapServer, mapLevel, 21.5f);
    /* acknowledges the asynchronous apply so clients never race a stale reset */
    IedServer_updateInt32AttributeValue(mapServer, mapCommand, 1000 + command);
    IedServer_unlockDataModel(mapServer);
}

static void* mapInput(void* parameter)
{
    (void)parameter;
    char line[32];
    while (fgets(line, sizeof(line), stdin))
        if (strncmp(line, "quit", 4) == 0) break;
    mapQuit = 1;
    return NULL;
}

static int mapServerMain(const char* bindAddress, int port)
{
    IedModel* model = IedModel_create("NFTEST");
    LogicalDevice* device = LogicalDevice_create("LD0", model);
    LogicalNode_create("LLN0", device);
    LogicalNode* node = LogicalNode_create("GGIO1", device);
    DataObject* status = DataObject_create("Status", (ModelNode*)node, 0);
    mapStatus = DataAttribute_create("stVal", (ModelNode*)status, IEC61850_BOOLEAN, IEC61850_FC_ST, TRG_OPT_DATA_CHANGED, 0, 0);
    mapQuality = DataAttribute_create("q", (ModelNode*)status, IEC61850_QUALITY, IEC61850_FC_ST, TRG_OPT_QUALITY_CHANGED, 0, 0);
    mapTime = DataAttribute_create("t", (ModelNode*)status, IEC61850_TIMESTAMP, IEC61850_FC_ST, TRG_OPT_DATA_CHANGED, 0, 0);
    DataObject* pos = DataObject_create("Pos", (ModelNode*)node, 0);
    mapPos = DataAttribute_create("stVal", (ModelNode*)pos, IEC61850_CODEDENUM, IEC61850_FC_ST, TRG_OPT_DATA_CHANGED, 0, 0);
    DataObject* level = DataObject_create("Level", (ModelNode*)node, 0);
    mapLevel = DataAttribute_create("mag", (ModelNode*)level, IEC61850_FLOAT32, IEC61850_FC_MX, TRG_OPT_DATA_CHANGED, 0, 0);
    DataObject* target = DataObject_create("Target", (ModelNode*)node, 0);
    mapTarget = DataAttribute_create("setVal", (ModelNode*)target, IEC61850_INT32, IEC61850_FC_SP, TRG_OPT_DATA_CHANGED, 0, 0);
    DataObject* gain = DataObject_create("Gain", (ModelNode*)node, 0);
    mapGain = DataAttribute_create("setVal", (ModelNode*)gain, IEC61850_FLOAT32, IEC61850_FC_SP, 0, 0, 0);
    DataObject* command = DataObject_create("Command", (ModelNode*)node, 0);
    mapCommand = DataAttribute_create("setVal", (ModelNode*)command, IEC61850_INT32, IEC61850_FC_SP, 0, 0, 0);
    DataObject* writes = DataObject_create("Writes", (ModelNode*)node, 0);
    mapWrites = DataAttribute_create("stVal", (ModelNode*)writes, IEC61850_INT32, IEC61850_FC_ST, TRG_OPT_DATA_CHANGED, 0, 0);
    DataObject* unmapped = DataObject_create("Unmapped", (ModelNode*)node, 0);
    mapUnmapped = DataAttribute_create("stVal", (ModelNode*)unmapped, IEC61850_INT32, IEC61850_FC_ST, TRG_OPT_DATA_CHANGED, 0, 0);
    DataSet* dataSet = DataSet_create("Events", node);
    DataSetEntry_create(dataSet, MAP_WRITES, -1, NULL);
    DataSetEntry_create(dataSet, MAP_UNMAPPED, -1, NULL);
    DataSetEntry_create(dataSet, MAP_LEVEL, -1, NULL);
    DataSetEntry_create(dataSet, MAP_TIME, -1, NULL);
    DataSetEntry_create(dataSet, MAP_STATUS, -1, NULL);
    DataSetEntry_create(dataSet, MAP_QUALITY, -1, NULL);
    DataSetEntry_create(dataSet, MAP_POS, -1, NULL);
    DataSetEntry_create(dataSet, MAP_TARGET, -1, NULL);
    ReportControlBlock_create("Events", node, "NFMAPREPORT", false, "Events", 1,
        TRG_OPT_DATA_CHANGED | TRG_OPT_QUALITY_CHANGED | TRG_OPT_GI,
        RPT_OPT_SEQ_NUM | RPT_OPT_REASON_FOR_INCLUSION | RPT_OPT_DATA_SET | RPT_OPT_CONF_REV, 0, 0);
    mapServer = IedServer_create(model);
    IedServer_setConnectionIndicationHandler(mapServer, mapConnection, NULL);
    IedServer_handleWriteAccess(mapServer, mapTarget, mapWrite, NULL);
    IedServer_handleWriteAccess(mapServer, mapGain, mapWrite, NULL);
    IedServer_handleWriteAccess(mapServer, mapCommand, mapWrite, NULL);
    IedServer_setLocalIpAddress(mapServer, bindAddress);
    IedServer_start(mapServer, port);
    if (!IedServer_isRunning(mapServer)) {
        fprintf(stderr, "listen failed\n");
        IedServer_destroy(mapServer);
        IedModel_destroy(model);
        return 1;
    }
    mapApply(0);
    mapEpoch = Hal_getTimeInMs();
    puts("READY");
    fflush(stdout);
    Thread input = Thread_create(mapInput, NULL, false);
    Thread_start(input);
    int published = 0;
    while (!mapQuit) {
        int pending = mapPending;
        if (pending >= 0) { mapApply(pending); mapPending = -1; mapTrace("command %i applied", pending); }
        int accepted = mapAccepted;
        if (accepted != published) {
            IedServer_lockDataModel(mapServer);
            IedServer_updateInt32AttributeValue(mapServer, mapWrites, accepted);
            IedServer_unlockDataModel(mapServer);
            published = accepted;
        }
        Thread_sleep(5);
    }
    Thread_destroy(input);
    IedServer_stop(mapServer);
    IedServer_destroy(mapServer);
    IedModel_destroy(model);
    printf("NATIVE MAP SERVER PASS writes=%i reports=%i commands=%i\n", mapAccepted, mapReportsSent, mapCommands);
    return 0;
}

enum { MAP_K_STATUS, MAP_K_QUALITY, MAP_K_TIME, MAP_K_POS, MAP_K_LEVEL, MAP_K_TARGET, MAP_K_WRITES, MAP_K_UNMAPPED, MAP_K_OTHER };
static const char* mapKeys[] = { MAP_STATUS, MAP_QUALITY, MAP_TIME, MAP_POS, MAP_LEVEL, MAP_TARGET, MAP_WRITES, MAP_UNMAPPED };

typedef struct {
    int status; uint16_t quality; uint64_t time; uint8_t timeQuality; int pos; float level;
    int32_t target; float gain; int32_t writes; int32_t unmapped;
} MapState;

typedef struct {
    int keys[16]; int count; MapState expected;
    volatile int seen; volatile int bad; volatile int reports;
} MapReportState;

static int mapCheck(int key, MmsValue* v, const MapState* s)
{
    if (!v) return 0;
    switch (key) {
    case MAP_K_STATUS: return MmsValue_getType(v) == MMS_BOOLEAN && MmsValue_getBoolean(v) == (s->status != 0);
    case MAP_K_QUALITY: return MmsValue_getType(v) == MMS_BIT_STRING && Quality_fromMmsValue(v) == s->quality;
    case MAP_K_TIME: return MmsValue_getType(v) == MMS_UTC_TIME && MmsValue_getUtcTimeInMs(v) == s->time &&
        MmsValue_getUtcTimeQuality(v) == s->timeQuality;
    case MAP_K_POS: return MmsValue_getType(v) == MMS_BIT_STRING && MmsValue_getBitStringSize(v) == 2 &&
        (int)MmsValue_getBitStringAsIntegerBigEndian(v) == s->pos;
    case MAP_K_LEVEL: return MmsValue_getType(v) == MMS_FLOAT && MmsValue_toFloat(v) == s->level;
    case MAP_K_TARGET: return MmsValue_getType(v) == MMS_INTEGER && MmsValue_toInt32(v) == s->target;
    case MAP_K_WRITES: return MmsValue_getType(v) == MMS_INTEGER && MmsValue_toInt32(v) == s->writes;
    case MAP_K_UNMAPPED: return MmsValue_getType(v) == MMS_INTEGER && MmsValue_toInt32(v) == s->unmapped;
    }
    return 0;
}

static int mapQuiet;

static int mapReadKey(MmsConnection c, int key, const MapState* s)
{
    MmsError error;
    MmsValue* v = MmsConnection_readVariable(c, &error, MAP_LD, mapKeys[key]);
    int ok = error == MMS_ERROR_NONE && mapCheck(key, v, s);
    if (!ok && !mapQuiet) {
        char text[64] = "null";
        if (v) MmsValue_printToBuffer(v, text, sizeof(text));
        fprintf(stderr, "map read mismatch %s error=%i value=%s\n", mapKeys[key], error, text);
    }
    if (v) MmsValue_delete(v);
    return ok;
}

static int mapReadInt(MmsConnection c, const char* item, int32_t* result)
{
    MmsError error;
    MmsValue* v = MmsConnection_readVariable(c, &error, MAP_LD, item);
    int ok = error == MMS_ERROR_NONE && v && MmsValue_getType(v) == MMS_INTEGER;
    if (ok) *result = MmsValue_toInt32(v);
    if (v) MmsValue_delete(v);
    return ok;
}

static int mapWaitWrites(MmsConnection c, int32_t expected)
{
    uint64_t deadline = Hal_getTimeInMs() + 3000;
    int32_t actual = -1;
    while (Hal_getTimeInMs() < deadline) {
        if (mapReadInt(c, MAP_WRITES, &actual) && actual == expected) return 1;
        Thread_sleep(20);
    }
    fprintf(stderr, "writes expected=%i actual=%i\n", expected, actual);
    return 0;
}

static int mapVerify(MmsConnection c, const MapState* s, int iec);

static int mapWaitReset(MmsConnection c, MapState* s, int iec)
{
    uint64_t deadline = Hal_getTimeInMs() + 3000;
    int ok = 0;
    mapQuiet = 1;
    while (!ok && Hal_getTimeInMs() < deadline) {
        int32_t ack = -1;
        ok = mapReadInt(c, MAP_COMMAND, &ack) && ack == 1000 && mapReadInt(c, MAP_WRITES, &s->writes) && mapVerify(c, s, iec);
        if (!ok) Thread_sleep(20);
    }
    mapQuiet = 0;
    return ok;
}

static int mapVerify(MmsConnection c, const MapState* s, int iec)
{
    int ok = mapReadKey(c, MAP_K_STATUS, s) && mapReadKey(c, MAP_K_LEVEL, s) &&
        mapReadKey(c, MAP_K_TARGET, s) && mapReadKey(c, MAP_K_WRITES, s);
    if (ok && iec) ok = mapReadKey(c, MAP_K_QUALITY, s) && mapReadKey(c, MAP_K_TIME, s) && mapReadKey(c, MAP_K_POS, s);
    MmsError error;
    MmsValue* v = MmsConnection_readVariable(c, &error, MAP_LD, MAP_GAIN);
    ok = ok && error == MMS_ERROR_NONE && v && MmsValue_getType(v) == MMS_FLOAT && MmsValue_toFloat(v) == s->gain;
    if (v) MmsValue_delete(v);
    return ok;
}

static MmsDataAccessError mapWriteValue(MmsConnection c, MmsError* error, const char* item, MmsValue* value)
{
    MmsDataAccessError result = MmsConnection_writeVariable(c, error, MAP_LD, item, value);
    MmsValue_delete(value);
    return result;
}

static int mapMultiple(MmsConnection c, const MapState* s)
{
    MmsError error;
    LinkedList items = LinkedList_create();
    LinkedList_add(items, MAP_LEVEL);
    LinkedList_add(items, MAP_WRITES);
    LinkedList_add(items, "GGIO1$ST$Missing$stVal");
    LinkedList_add(items, MAP_STATUS);
    LinkedList_add(items, MAP_TARGET);
    MmsValue* values = MmsConnection_readMultipleVariables(c, &error, MAP_LD, items);
    LinkedList_destroyStatic(items);
    int ok = error == MMS_ERROR_NONE && values && MmsValue_getArraySize(values) == 5 &&
        mapCheck(MAP_K_LEVEL, MmsValue_getElement(values, 0), s) && mapCheck(MAP_K_WRITES, MmsValue_getElement(values, 1), s) &&
        MmsValue_getType(MmsValue_getElement(values, 2)) == MMS_DATA_ACCESS_ERROR &&
        MmsValue_getDataAccessError(MmsValue_getElement(values, 2)) == DATA_ACCESS_ERROR_OBJECT_NONE_EXISTENT &&
        mapCheck(MAP_K_STATUS, MmsValue_getElement(values, 3), s) && mapCheck(MAP_K_TARGET, MmsValue_getElement(values, 4), s);
    if (values) MmsValue_delete(values);
    return ok;
}

static void mapReport(void* parameter, ClientReport report)
{
    MapReportState* state = (MapReportState*)parameter;
    MmsValue* values = ClientReport_getDataSetValues(report);
    state->reports++;
    if (!values || MmsValue_getArraySize(values) != state->count) { state->bad++; return; }
    for (int i = 0; i < state->count; i++) {
        if (ClientReport_getReasonForInclusion(report, i) == IEC61850_REASON_NOT_INCLUDED) continue;
        if (state->keys[i] == MAP_K_OTHER || !mapCheck(state->keys[i], MmsValue_getElement(values, i), &state->expected)) {
            fprintf(stderr, "report entry %i mismatch\n", i);
            state->bad++;
        }
        else state->seen |= 1 << state->keys[i];
    }
}

static int mapWaitReport(MapReportState* state, int required)
{
    uint64_t deadline = Hal_getTimeInMs() + 5000;
    while ((state->seen & required) != required && state->bad == 0 && Hal_getTimeInMs() < deadline) Thread_sleep(5);
    return (state->seen & required) == required && state->bad == 0;
}

static int mapReports(IedConnection ied, MmsConnection mms, MapState* s)
{
    static MapReportState state;
    memset((void*)&state, 0, sizeof(state));
    MmsError error;
    IedClientError iedError = IED_ERROR_OK;
    bool deletable = true;
    int all = 0;
    LinkedList directory = MmsConnection_readNamedVariableListDirectory(mms, &error, MAP_LD, "GGIO1$Events", &deletable);
    int ok = error == MMS_ERROR_NONE && directory && !deletable;
    if (directory)
        for (LinkedList entry = LinkedList_getNext(directory); entry && ok; entry = LinkedList_getNext(entry)) {
            MmsVariableAccessSpecification* spec = (MmsVariableAccessSpecification*)LinkedList_getData(entry);
            ok = state.count < 16 && spec->domainId && strcmp(spec->domainId, MAP_LD) == 0;
            int key = MAP_K_OTHER;
            for (int k = 0; k < MAP_K_OTHER && ok; k++) if (strcmp(spec->itemId, mapKeys[k]) == 0) key = k;
            ok = ok && key != MAP_K_OTHER && !(all & (1 << key));
            all |= 1 << key;
            state.keys[state.count++] = key;
        }
    if (directory) LinkedList_destroyDeep(directory, (LinkedListValueDeleteFunction)MmsVariableAccessSpecification_destroy);
    ok = ok && state.count >= 7;
    state.expected = *s;
    ClientReportControlBlock rcb = ok ? IedConnection_getRCBValues(ied, &iedError, "NFTESTLD0/GGIO1.RP.Events", NULL) : NULL;
    ok = ok && iedError == IED_ERROR_OK && rcb;
    if (ok) {
        IedConnection_installReportHandler(ied, "NFTESTLD0/GGIO1.RP.Events", ClientReportControlBlock_getRptId(rcb), mapReport, &state);
        ClientReportControlBlock_setRptEna(rcb, true);
        IedConnection_setRCBValues(ied, &iedError, rcb, RCB_ELEMENT_RPT_ENA, false);
        ok = iedError == IED_ERROR_OK;
        ClientReportControlBlock_setGI(rcb, true);
        if (ok) IedConnection_setRCBValues(ied, &iedError, rcb, RCB_ELEMENT_GI, false);
        ok = ok && iedError == IED_ERROR_OK && mapWaitReport(&state, all);
        if (!ok) fprintf(stderr, "GI report failed ied=%i seen=%x all=%x bad=%i\n", iedError, state.seen, all, state.bad);
        if (ok) {
            s->status = 0; s->quality = QUALITY_VALIDITY_GOOD; s->time = 1700000001000ULL; s->timeQuality = 0x0a;
            s->pos = DBPOS_OFF; s->level = 20.5f; s->unmapped = 77;
            state.expected = *s;
            state.seen = 0;
            ok = mapWriteValue(mms, &error, MAP_COMMAND, MmsValue_newIntegerFromInt32(1)) == DATA_ACCESS_ERROR_SUCCESS;
            int changed = all & ((1 << MAP_K_STATUS) | (1 << MAP_K_QUALITY) | (1 << MAP_K_TIME) | (1 << MAP_K_POS) | (1 << MAP_K_LEVEL));
            ok = ok && changed == ((1 << MAP_K_STATUS) | (1 << MAP_K_QUALITY) | (1 << MAP_K_TIME) | (1 << MAP_K_POS) | (1 << MAP_K_LEVEL)) &&
                mapWaitReport(&state, changed);
            if (!ok) fprintf(stderr, "change report failed seen=%x bad=%i reports=%i\n", state.seen, state.bad, state.reports);
        }
        ClientReportControlBlock_setRptEna(rcb, false);
        IedConnection_setRCBValues(ied, &iedError, rcb, RCB_ELEMENT_RPT_ENA, false);
        ok = ok && iedError == IED_ERROR_OK;
        IedConnection_uninstallReportHandler(ied, "NFTESTLD0/GGIO1.RP.Events");
    }
    if (rcb) ClientReportControlBlock_destroy(rcb);
    return ok;
}

static int mapNamedList(MmsConnection mms, const MapState* s)
{
    MmsError error;
    bool deletable = true;
    const char* expected[] = { MAP_WRITES, MAP_LEVEL, MAP_STATUS };
    LinkedList directory = MmsConnection_readNamedVariableListDirectory(mms, &error, MAP_LD, "GGIO1$Events", &deletable);
    int ok = error == MMS_ERROR_NONE && directory && LinkedList_size(directory) == 3 && !deletable;
    int i = 0;
    if (ok)
        for (LinkedList entry = LinkedList_getNext(directory); entry; entry = LinkedList_getNext(entry), i++) {
            MmsVariableAccessSpecification* spec = (MmsVariableAccessSpecification*)LinkedList_getData(entry);
            ok = ok && spec->domainId && strcmp(spec->domainId, MAP_LD) == 0 && strcmp(spec->itemId, expected[i]) == 0;
        }
    if (directory) LinkedList_destroyDeep(directory, (LinkedListValueDeleteFunction)MmsVariableAccessSpecification_destroy);
    MmsValue* values = ok ? MmsConnection_readNamedVariableListValues(mms, &error, MAP_LD, "GGIO1$Events", true) : NULL;
    ok = ok && error == MMS_ERROR_NONE && values && MmsValue_getArraySize(values) == 3 &&
        mapCheck(MAP_K_WRITES, MmsValue_getElement(values, 0), s) && mapCheck(MAP_K_LEVEL, MmsValue_getElement(values, 1), s) &&
        mapCheck(MAP_K_STATUS, MmsValue_getElement(values, 2), s);
    if (values) MmsValue_delete(values);
    return ok;
}

static int mapOpen(IedConnection* ied, MmsConnection* mms, const char* host, int port, int iec)
{
    MmsError error = MMS_ERROR_NONE;
    IedClientError iedError = IED_ERROR_OK;
    if (iec) {
        *ied = IedConnection_create();
        *mms = IedConnection_getMmsConnection(*ied);
        IedConnection_setConnectTimeout(*ied, 5000);
        IedConnection_setRequestTimeout(*ied, 2500);
        IedConnection_connect(*ied, &iedError, host, port);
        if (iedError == IED_ERROR_OK) return 1;
    }
    else {
        *ied = NULL;
        *mms = MmsConnection_create();
        MmsConnection_setConnectTimeout(*mms, 5000);
        MmsConnection_setRequestTimeout(*mms, 2500);
        if (MmsConnection_connect(*mms, &error, host, port)) return 1;
    }
    fprintf(stderr, "map connect failed ied=%i mms=%i\n", iedError, error);
    return 0;
}

static void mapClose(IedConnection* ied, MmsConnection* mms, int graceful)
{
    if (*ied) {
        IedClientError error;
        if (graceful) IedConnection_release(*ied, &error);
        IedConnection_close(*ied);
        IedConnection_destroy(*ied);
    }
    else if (*mms) {
        MmsError error;
        if (graceful) MmsConnection_conclude(*mms, &error);
        MmsConnection_close(*mms);
        MmsConnection_destroy(*mms);
    }
    *ied = NULL; *mms = NULL;
}

#define MAP_REQUIRE(condition, step) do { if (!(condition)) { failed = step; goto done; } } while (0)

static int mapClient(const char* host, int port, int iec)
{
    IedConnection ied = NULL;
    MmsConnection mms = NULL;
    MmsError error = MMS_ERROR_NONE;
    const char* failed = NULL;
    int32_t base = 0;
    MapState s = { 1, MAP_INITIAL_QUALITY, 1700000000125ULL, 0x3f, DBPOS_ON, 12.5f, 0, 1.0f, 0, 0 };
    MAP_REQUIRE(mapOpen(&ied, &mms, host, port, iec), "connect");
    MAP_REQUIRE(mapWriteValue(mms, &error, MAP_COMMAND, MmsValue_newIntegerFromInt32(0)) == DATA_ACCESS_ERROR_SUCCESS, "reset");
    MAP_REQUIRE(mapWaitReset(mms, &s, iec), "reset applied");
    base = s.writes;
    MAP_REQUIRE(mapVerify(mms, &s, iec) && mapMultiple(mms, &s), "initial");
    MAP_REQUIRE(mapWriteValue(mms, &error, MAP_TARGET, MmsValue_newIntegerFromInt32(5)) == DATA_ACCESS_ERROR_SUCCESS, "target");
    s.target = 5; s.writes = base + 1;
    MAP_REQUIRE(mapWaitWrites(mms, s.writes) && mapVerify(mms, &s, iec), "target readback");
    MAP_REQUIRE(mapWriteValue(mms, &error, MAP_TARGET, MmsValue_newIntegerFromInt32(1001)) == DATA_ACCESS_ERROR_OBJECT_VALUE_INVALID, "range");
    MAP_REQUIRE(mapWriteValue(mms, &error, MAP_GAIN, MmsValue_newFloat(NAN)) == DATA_ACCESS_ERROR_OBJECT_VALUE_INVALID, "nan");
    MAP_REQUIRE(mapWriteValue(mms, &error, MAP_TARGET, MmsValue_newBoolean(true)) == DATA_ACCESS_ERROR_TYPE_INCONSISTENT, "type");
    MAP_REQUIRE(mapWriteValue(mms, &error, MAP_STATUS, MmsValue_newBoolean(false)) == DATA_ACCESS_ERROR_OBJECT_ACCESS_DENIED, "readonly");
    MAP_REQUIRE(mapWriteValue(mms, &error, "GGIO1$ST$Missing$stVal", MmsValue_newIntegerFromInt32(1)) ==
        DATA_ACCESS_ERROR_OBJECT_NONE_EXISTENT, "missing");
    MAP_REQUIRE(mapVerify(mms, &s, iec), "rejections preserved");
    MAP_REQUIRE(mapWriteValue(mms, &error, MAP_GAIN, MmsValue_newFloat(2.5f)) == DATA_ACCESS_ERROR_SUCCESS, "gain");
    s.gain = 2.5f;
    MAP_REQUIRE(mapVerify(mms, &s, iec), "gain readback");
    if (iec) MAP_REQUIRE(mapReports(ied, mms, &s), "report");
    else {
        MAP_REQUIRE(mapWriteValue(mms, &error, MAP_COMMAND, MmsValue_newIntegerFromInt32(1)) == DATA_ACCESS_ERROR_SUCCESS, "change");
        s.status = 0; s.level = 20.5f;
        MAP_REQUIRE(mapNamedList(mms, &s), "named list after domain report");
    }
    MAP_REQUIRE(mapVerify(mms, &s, iec), "changed readback");
    mapWriteValue(mms, &error, MAP_TARGET, MmsValue_newIntegerFromInt32(999));
    MAP_REQUIRE(error == MMS_ERROR_SERVICE_TIMEOUT, "response loss");
    mapClose(&ied, &mms, 0);
    Thread_sleep(4000);
    MAP_REQUIRE(mapOpen(&ied, &mms, host, port, iec), "reconnect");
    s.target = 999; s.writes = base + 2;
    MAP_REQUIRE(mapWaitWrites(mms, s.writes) && mapVerify(mms, &s, iec), "single side effect");
    Thread_sleep(1000);
    MAP_REQUIRE(mapVerify(mms, &s, iec), "no replay");
done:
    if (failed) fprintf(stderr, "NATIVE MAP %s CLIENT FAIL step=%s mms=%i\n", iec ? "IEC" : "MMS", failed, error);
    else printf("NATIVE MAP %s CLIENT PASS writes=%i base=%i\n", iec ? "IEC" : "MMS", s.writes, base);
    mapClose(&ied, &mms, failed == NULL);
    return failed ? 1 : 0;
}

int main(int argc, char** argv)
{
    if (argc == 2 && strcmp(argv[1], "--identity") == 0) {
        puts("libiec61850 v1.6.2 ORIGINAL f0f797bf955da763353f7081c892a1ff6b55e5a1");
        return 0;
    }
    if (argc < 3 || argc > 5) return 2;
    int port = atoi(argv[2]);
    if (port < 1024 || port > 65535) return 2;
    if (strcmp(argv[1], "mapiecclient") == 0 || strcmp(argv[1], "mapmmsclient") == 0) {
        if (argc != 4) return 2;
        return mapClient(argv[3], port, strcmp(argv[1], "mapiecclient") == 0);
    }
    if (strcmp(argv[1], "typedclient") == 0) {
        if (argc != 4) return 2;
        IedConnection connection = IedConnection_create();
        IedClientError error;
        IedConnection_connect(connection, &error, argv[3], port);
        int ok = error == IED_ERROR_OK && typedActions(connection);
        if (ok) { IedConnection_release(connection, &error); ok = error == IED_ERROR_OK; }
        IedConnection_close(connection); IedConnection_destroy(connection);
        return ok ? 0 : 1;
    }
    if (strcmp(argv[1], "releaseclient") == 0) {
        if (argc != 4) return 2;
        MmsConnection client = MmsConnection_create();
        MmsError error;
        int ok = MmsConnection_connect(client, &error, argv[3], port);
        if (ok) { MmsConnection_conclude(client, &error); ok = error == MMS_ERROR_NONE; Thread_sleep(200); }
        MmsConnection_close(client); MmsConnection_destroy(client);
        if (ok) puts("NATIVE RELEASE PASS");
        return ok ? 0 : 1;
    }
    if (strcmp(argv[1], "dataclient") == 0) {
        if (argc != 4) return 2;
        IedConnection connection = IedConnection_create();
        IedClientError error;
        IedConnection_connect(connection, &error, argv[3], port);
        int ok = error == IED_ERROR_OK && dataActions(connection);
        IedConnection_close(connection); IedConnection_destroy(connection);
        return ok ? 0 : 1;
    }
    if (strcmp(argv[1], "sboenhancedstress") == 0) {
        if (argc != 5) return 2;
        int repetitions = atoi(argv[4]);
        if (repetitions < 1 || repetitions > 1024) return 2;
        IedConnection connection = IedConnection_create();
        IedClientError error;
        IedConnection_connect(connection, &error, argv[3], port);
        int ok = error == IED_ERROR_OK && sboEnhancedStress(connection, repetitions);
        IedConnection_close(connection); IedConnection_destroy(connection);
        return ok ? 0 : 1;
    }
    if (strcmp(argv[1], "enhancedclient") == 0 || strcmp(argv[1], "sboenhancedclient") == 0) {
        if (argc != 4) return 2;
        IedConnection connection = IedConnection_create();
        IedClientError error;
        IedConnection_connect(connection, &error, argv[3], port);
        int ok = error == IED_ERROR_OK && enhancedActions(connection, strcmp(argv[1], "sboenhancedclient") == 0);
        IedConnection_close(connection); IedConnection_destroy(connection);
        return ok ? 0 : 1;
    }
    if (strcmp(argv[1], "controlstress") == 0) {
        if (argc != 5) return 2;
        int repetitions = atoi(argv[4]);
        if (repetitions < 2 || repetitions > 4096 || (repetitions & 1)) return 2;
        IedConnection connection = IedConnection_create();
        IedClientError error;
        IedConnection_connect(connection, &error, argv[3], port);
        int ok = error == IED_ERROR_OK && controlStress(connection, repetitions);
        IedConnection_close(connection); IedConnection_destroy(connection);
        return ok ? 0 : 1;
    }
    if (strcmp(argv[1], "controlclient") == 0) {
        if (argc != 4 && argc != 5) return 2;
        IedConnection connection = IedConnection_create();
        IedClientError error;
        IedConnection_connect(connection, &error, argv[3], port);
        int ok = error == IED_ERROR_OK && controlActions(connection, argc == 5);
        IedConnection_close(connection); IedConnection_destroy(connection);
        return ok ? 0 : 1;
    }
    if (strcmp(argv[1], "complexverify") == 0) {
        if (argc != 4) return 2;
        MmsConnection client = MmsConnection_create();
        MmsError error;
        int ok = MmsConnection_connect(client, &error, argv[3], port);
        if (ok) ok = verifyComposite(client);
        MmsConnection_close(client);
        MmsConnection_destroy(client);
        if (!ok) { fprintf(stderr, "composite verification failed\n"); return 1; }
        puts("NATIVE COMPOSITE PASS");
        return 0;
    }
    if (strcmp(argv[1], "verify") == 0 || strcmp(argv[1], "controlverify") == 0) {
        if (argc != 5) return 2;
        int sequence = atoi(argv[4]);
        if (sequence < 1 || sequence > 100) return 2;
        int controls = strcmp(argv[1], "controlverify") == 0;
        IedConnection ied = controls ? IedConnection_create() : NULL;
        MmsConnection client = controls ? IedConnection_getMmsConnection(ied) : MmsConnection_create();
        MmsError error = MMS_ERROR_NONE;
        bool connected;
        if (ied) {
            IedClientError iedError;
            IedConnection_connect(ied, &iedError, argv[3], port);
            connected = iedError == IED_ERROR_OK;
        }
        else connected = MmsConnection_connect(client, &error, argv[3], port);
        if (!connected) {
            fprintf(stderr, "verify connect error %i\n", error);
            if (ied) IedConnection_destroy(ied);
            else MmsConnection_destroy(client);
            return 1;
        }
        int ok = discover(client, controls ? 17 : 4);
        if (ok) ok = verifyComposite(client);
        if (ok) ok = verifyWrites(client);
        if (ok && controls) ok = controlActions(ied, true);
        for (int i = 0; i < 10 && ok; i++) {
            MmsValue* v = MmsConnection_readVariable(client, &error, "NFTESTLD0", "GGIO1$ST$Status$stVal");
            ok = error == MMS_ERROR_NONE && v && MmsValue_getType(v) == MMS_BOOLEAN &&
                MmsValue_getBoolean(v) == (sequence % 2 != 0);
            if (v) MmsValue_delete(v);
            v = MmsConnection_readVariable(client, &error, "NFTESTLD0", "GGIO1$ST$Count$stVal");
            ok = ok && error == MMS_ERROR_NONE && v && MmsValue_getType(v) == MMS_INTEGER &&
                MmsValue_toInt32(v) == -123456 + sequence;
            if (v) MmsValue_delete(v);
            v = MmsConnection_readVariable(client, &error, "NFTESTLD0", "GGIO1$MX$Analog$mag");
            ok = ok && error == MMS_ERROR_NONE && v && MmsValue_getType(v) == MMS_FLOAT &&
                MmsValue_toFloat(v) == 12.5f + sequence;
            if (v) MmsValue_delete(v);
            v = MmsConnection_readVariable(client, &error, "NFTESTLD0", "GGIO1$ST$Missing$stVal");
            ok = ok && error == MMS_ERROR_NONE && v && MmsValue_getType(v) == MMS_DATA_ACCESS_ERROR &&
                MmsValue_getDataAccessError(v) == DATA_ACCESS_ERROR_OBJECT_NONE_EXISTENT;
            if (v) MmsValue_delete(v);
        }
        MmsConnection_close(client);
        if (ied) IedConnection_destroy(ied);
        else MmsConnection_destroy(client);
        if (!ok) { fprintf(stderr, "verify read mismatch: %i\n", error); return 1; }
        printf("NATIVE SERVER VERIFY PASS sequence=%i scalar=30 access=10\n", sequence);
        return 0;
    }
    if (strcmp(argv[1], "client") == 0) {
        if (argc != 3) return 2;
        MmsConnection client = MmsConnection_create();
        MmsError error;
        if (!MmsConnection_connect(client, &error, "127.0.0.1", port)) {
            fprintf(stderr, "connect error %i\n", error);
            MmsConnection_destroy(client);
            return 1;
        }
        MmsValue* value = MmsConnection_readVariable(client, &error, "NFTESTLD0", "GGIO1$ST$Status$stVal");
        int ok = error == MMS_ERROR_NONE && value && MmsValue_getType(value) == MMS_BOOLEAN && MmsValue_getBoolean(value);
        if (value) MmsValue_delete(value);
        ok = ok && discover(client, 0);
        MmsConnection_close(client);
        MmsConnection_destroy(client);
        if (!ok) { fprintf(stderr, "read failed %i\n", error); return 1; }
        puts("NATIVE CLIENT PASS");
        return 0;
    }
    if ((argc != 3 && argc != 4) || (strcmp(argv[1], "server") != 0 &&
        strcmp(argv[1], "complexserver") != 0 && strcmp(argv[1], "writeserver") != 0 && strcmp(argv[1], "controlserver") != 0 &&
        strcmp(argv[1], "enhancedserver") != 0 && strcmp(argv[1], "sboenhancedserver") != 0 &&
        strcmp(argv[1], "dataserver") != 0 && strcmp(argv[1], "typedserver") != 0 && strcmp(argv[1], "silent") != 0 &&
        strcmp(argv[1], "mapserver") != 0)) return 2;
    const char* bindAddress = argc == 4 ? argv[3] : "127.0.0.1";
    unsigned int a, b, c, d;
    char extra;
    if (sscanf(bindAddress, "%u.%u.%u.%u%c", &a, &b, &c, &d, &extra) != 4 ||
        a > 255 || b > 255 || c > 255 || d > 255 || !(a || b || c || d)) {
        fprintf(stderr, "explicit non-wildcard IPv4 required\n");
        return 2;
    }
    if (strcmp(argv[1], "silent") == 0) return silentServer(bindAddress, port);
    if (strcmp(argv[1], "mapserver") == 0) return mapServerMain(bindAddress, port);
    IedModel* model = IedModel_create("NFTEST");
    LogicalDevice* device = LogicalDevice_create("LD0", model);
    LogicalNode_create("LLN0", device);
    LogicalNode* node = LogicalNode_create("GGIO1", device);
    if (strcmp(argv[1], "dataserver") == 0) {
        DataSet* dataSet = DataSet_create("Measurements", node);
        DataSetEntry_create(dataSet, "GGIO1$ST$Status$stVal", -1, NULL);
        DataSetEntry_create(dataSet, "GGIO1$ST$Count$stVal", -1, NULL);
        DataSetEntry_create(dataSet, "GGIO1$MX$Analog$mag", -1, NULL);
        ReportControlBlock_create("Events", node, "NFREPORT", false, "Measurements", 1,
            TRG_OPT_DATA_CHANGED | TRG_OPT_INTEGRITY | TRG_OPT_GI,
            RPT_OPT_SEQ_NUM | RPT_OPT_REASON_FOR_INCLUSION | RPT_OPT_DATA_SET | RPT_OPT_CONF_REV, 0, 0);
    }
    DataObject* status = DataObject_create("Status", (ModelNode*)node, 0);
    DataAttribute* flag = DataAttribute_create("stVal", (ModelNode*)status, IEC61850_BOOLEAN, IEC61850_FC_ST, 0, 0, 0);
    DataAttribute* quality = NULL;
    DataAttribute* timestamp = NULL;
    if (strcmp(argv[1], "dataserver") == 0) {
        quality = DataAttribute_create("q", (ModelNode*)status, IEC61850_QUALITY, IEC61850_FC_ST, 0, 0, 0);
        timestamp = DataAttribute_create("t", (ModelNode*)status, IEC61850_TIMESTAMP, IEC61850_FC_ST, 0, 0, 0);
    }
    DataObject* count = DataObject_create("Count", (ModelNode*)node, 0);
    DataAttribute* integer = DataAttribute_create("stVal", (ModelNode*)count, IEC61850_INT32, IEC61850_FC_ST, 0, 0, 0);
    DataObject* analog = DataObject_create("Analog", (ModelNode*)node, 0);
    DataAttribute* real = DataAttribute_create("mag", (ModelNode*)analog, IEC61850_FLOAT32, IEC61850_FC_MX, 0, 0, 0);
    DataAttribute* samples = NULL;
    if (strcmp(argv[1], "complexserver") == 0 || strcmp(argv[1], "writeserver") == 0 || strcmp(argv[1], "controlserver") == 0) {
        DataObject* wave = DataObject_create("Samples", (ModelNode*)node, 0);
        samples = DataAttribute_create("values", (ModelNode*)wave, IEC61850_INT32, IEC61850_FC_MX, 0, 3, 0);
    }
    DataAttribute* writeFlag = NULL;
    DataAttribute* writeCount = NULL;
    DataAttribute* writeReal = NULL;
    if (strcmp(argv[1], "writeserver") == 0 || strcmp(argv[1], "controlserver") == 0) {
        DataObject* writable = DataObject_create("Writable", (ModelNode*)node, 0);
        writeFlag = DataAttribute_create("flag", (ModelNode*)writable, IEC61850_BOOLEAN, IEC61850_FC_SP, 0, 0, 0);
        writeCount = DataAttribute_create("count", (ModelNode*)writable, IEC61850_INT32, IEC61850_FC_SP, 0, 0, 0);
        writeReal = DataAttribute_create("real", (ModelNode*)writable, IEC61850_FLOAT32, IEC61850_FC_SP, 0, 0, 0);
    }
    DataObject* direct = NULL;
    DataObject* selected = NULL;
    DataObject* enhanced = NULL;
    if (strcmp(argv[1], "enhancedserver") == 0)
        enhanced = CDC_SPC_create("Enhanced", (ModelNode*)node, 0, CDC_CTL_MODEL_DIRECT_ENHANCED);
    if (strcmp(argv[1], "sboenhancedserver") == 0) {
        enhanced = CDC_SPC_create("SboEnhanced", (ModelNode*)node, 0,
            CDC_CTL_MODEL_SBO_ENHANCED | CDC_CTL_MODEL_HAS_CANCEL | CDC_CTL_OPTION_SBO_TIMEOUT);
        DataAttribute* timeout = (DataAttribute*)ModelNode_getChild((ModelNode*)enhanced, "sboTimeout");
        if (!timeout) timeout = DataAttribute_create("sboTimeout", (ModelNode*)enhanced, IEC61850_INT32U, IEC61850_FC_CF, 0, 0, 0);
        DataAttribute_setValue(timeout, MmsValue_newUnsignedFromUint32(1000));
    }
    if (strcmp(argv[1], "controlserver") == 0) {
        direct = CDC_SPC_create("Direct", (ModelNode*)node, 0, CDC_CTL_MODEL_DIRECT_NORMAL | CDC_CTL_MODEL_HAS_CANCEL);
        selected = CDC_SPC_create("Selected", (ModelNode*)node, 0,
            CDC_CTL_MODEL_SBO_NORMAL | CDC_CTL_MODEL_HAS_CANCEL | CDC_CTL_OPTION_SBO_TIMEOUT);
        DataAttribute* timeout = (DataAttribute*)ModelNode_getChild((ModelNode*)selected, "sboTimeout");
        if (!timeout) timeout = DataAttribute_create("sboTimeout", (ModelNode*)selected, IEC61850_INT32U, IEC61850_FC_CF, 0, 0, 0);
        DataAttribute_setValue(timeout, MmsValue_newUnsignedFromUint32(1000));
    }
    DataObject* typed[8] = { 0 };
    if (strcmp(argv[1], "typedserver") == 0) {
        for (int kind = 1; kind <= 2; kind++)
        for (int mode = 1; mode <= 4; mode++) {
            char name[8]; snprintf(name, sizeof(name), "%c%i", kind == 1 ? 'D' : 'A', mode);
            uint32_t options = mode | CDC_CTL_MODEL_HAS_CANCEL | CDC_CTL_OPTION_SBO_TIMEOUT;
            typed[(kind - 1) * 4 + mode - 1] = kind == 1 ?
                CDC_DPC_create(name, (ModelNode*)node, 0, options) : CDC_APC_create(name, (ModelNode*)node, 0, options, false);
        }
    }
    IedServer server = IedServer_create(model);
    if (strcmp(argv[1], "typedserver") == 0)
        for (int i = 0; i < 8; i++) IedServer_setControlHandler(server, typed[i], typedControl, server);
    if (enhanced) IedServer_setControlHandler(server, enhanced, enhancedControl, server);
    if (direct) {
        IedServer_setControlHandler(server, direct, memoryControl, server);
        IedServer_setControlHandler(server, selected, memoryControl, server);
    }
    if (strcmp(argv[1], "writeserver") == 0 || strcmp(argv[1], "controlserver") == 0) {
        IedServer_handleWriteAccess(server, writeFlag, allowScalarWrite, NULL);
        IedServer_handleWriteAccess(server, writeCount, allowScalarWrite, NULL);
        IedServer_handleWriteAccess(server, writeReal, allowScalarWrite, NULL);
        IedServer_updateBooleanAttributeValue(server, writeFlag, true);
        IedServer_updateInt32AttributeValue(server, writeCount, -123456);
        IedServer_updateFloatAttributeValue(server, writeReal, 12.5f);
    }
    IedServer_setLocalIpAddress(server, bindAddress);
    IedServer_updateBooleanAttributeValue(server, flag, true);
    IedServer_updateInt32AttributeValue(server, integer, -123456);
    IedServer_updateFloatAttributeValue(server, real, 12.5f);
    if (quality) {
        IedServer_updateQuality(server, quality, QUALITY_VALIDITY_INVALID | QUALITY_TEST | QUALITY_DETAIL_OLD_DATA);
        MmsValue* utc = MmsValue_newUtcTimeByMsTime(1700000000125ULL);
        MmsValue_setUtcTimeQuality(utc, 0x3f);
        IedServer_updateAttributeValue(server, timestamp, utc);
        MmsValue_delete(utc);
    }
    if (samples) {
        MmsValue* values = MmsValue_createEmptyArray(3);
        for (int i = 0; i < 3; i++) MmsValue_setElement(values, i, MmsValue_newIntegerFromInt32(100 + i));
        IedServer_updateAttributeValue(server, samples, values);
        MmsValue_delete(values);
    }
    IedServer_start(server, port);
    if (!IedServer_isRunning(server)) {
        fprintf(stderr, "listen failed\n");
        IedServer_destroy(server);
        IedModel_destroy(model);
        return 1;
    }
    puts("READY");
    fflush(stdout);
    char line[32];
    while (fgets(line, sizeof(line), stdin)) {
        if (strncmp(line, "quit", 4) == 0) break;
    }
    IedServer_stop(server);
    IedServer_destroy(server);
    IedModel_destroy(model);
    return 0;
}
