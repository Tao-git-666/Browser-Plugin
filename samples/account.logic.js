var AccountLogic = (function () {
  function onLoad(executionContext) {
    var formContext = executionContext.getFormContext();
    var status = formContext.getAttribute("statuscode").getValue();
    formContext.getControl("creditlimit").setVisible(status === 1);
    formContext.getAttribute("description").setRequiredLevel("recommended");
    formContext.getAttribute("creditlimit").addOnChange(onCreditLimitChange);
  }

  function onCreditLimitChange(executionContext) {
    var formContext = executionContext.getFormContext();
    var limit = formContext.getAttribute("creditlimit").getValue() || 0;
    formContext.getAttribute("new_requiresapproval").setValue(limit > 100000);
  }

  function onSave(executionContext) {
    var formContext = executionContext.getFormContext();
    if (formContext.getAttribute("new_requiresapproval").getValue()) {
      formContext.ui.setFormNotification("保存后将进入额度审批。", "INFO", "approval");
    }
  }

  async function submitForApproval(primaryControl) {
    var id = primaryControl.data.entity.getId().replace(/[{}]/g, "");
    await Xrm.WebApi.updateRecord("account", id, { new_submitted: true });
  }

  return {
    onLoad: onLoad,
    onSave: onSave,
    onCreditLimitChange: onCreditLimitChange,
    submitForApproval: submitForApproval
  };
})();

